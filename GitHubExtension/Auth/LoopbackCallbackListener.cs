// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BaldBeardedBuilder.CmdPal.GitHub.Auth;

internal sealed record OAuthCallback(string? Code, string? State, string? Error, string? ErrorDescription);

/// <summary>
/// Listens on 127.0.0.1 for the browser redirect at the end of the OAuth flow.
/// A raw TcpListener avoids http.sys URL ACLs and keeps us AOT friendly.
/// </summary>
internal sealed partial class LoopbackCallbackListener : IDisposable
{
    public const string CallbackPath = "/callback";

    private const string SuccessHtml = """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><title>Signed in</title>
        <style>body{font-family:Segoe UI,sans-serif;display:grid;place-items:center;height:100vh;margin:0;background:#0d1117;color:#e6edf3}</style>
        </head><body><div><h1>You're signed in</h1><p>You can close this tab and head back to Command Palette.</p></div></body></html>
        """;

    private const string FailureHtml = """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><title>Sign in failed</title>
        <style>body{font-family:Segoe UI,sans-serif;display:grid;place-items:center;height:100vh;margin:0;background:#0d1117;color:#e6edf3}</style>
        </head><body><div><h1>Sign in didn't finish</h1><p>Close this tab and try again from Command Palette.</p></div></body></html>
        """;

    private readonly TcpListener _listener;

    public LoopbackCallbackListener()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public Uri RedirectUri => new($"http://127.0.0.1:{Port}{CallbackPath}");

    public async Task<OAuthCallback> WaitForCallbackAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();

            var target = await ReadRequestTargetAsync(stream, cancellationToken).ConfigureAwait(false);
            if (target is null || !target.StartsWith(CallbackPath, StringComparison.Ordinal))
            {
                // Browsers also ask for things like /favicon.ico. Answer and keep waiting.
                await WriteResponseAsync(stream, "404 Not Found", string.Empty, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var callback = ParseCallback(target);
            var html = callback.Code is not null && callback.Error is null ? SuccessHtml : FailureHtml;
            await WriteResponseAsync(stream, "200 OK", html, cancellationToken).ConfigureAwait(false);
            return callback;
        }
    }

    public void Dispose() => _listener.Stop();

    internal static OAuthCallback ParseCallback(string requestTarget)
    {
        var queryStart = requestTarget.IndexOf('?', StringComparison.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (queryStart >= 0)
        {
            foreach (var pair in requestTarget[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                var key = Uri.UnescapeDataString((separator < 0 ? pair : pair[..separator]).Replace('+', ' '));
                var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
                values.TryAdd(key, value);
            }
        }

        return new OAuthCallback(
            values.GetValueOrDefault("code"),
            values.GetValueOrDefault("state"),
            values.GetValueOrDefault("error"),
            values.GetValueOrDefault("error_description"));
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // We only need the request line, like "GET /callback?code=...&state=... HTTP/1.1".
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(buffer, 0, total);
        var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        var requestLine = lineEnd >= 0 ? text[..lineEnd] : text;
        var parts = requestLine.Split(' ');
        return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string html, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
