// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BaldBeardedBuilder.CmdPal.GitHub.Api;

namespace BaldBeardedBuilder.CmdPal.GitHub.Actions;

internal sealed record GitHubWorkflow(long Id, string Name, string Path, string State);
internal sealed record WorkflowPageResult(IReadOnlyList<GitHubWorkflow> Workflows, Uri? NextPage);
internal sealed record WorkflowDispatchContext(IReadOnlyList<string> Refs, string DefaultRef);

internal sealed record WorkflowInputDefinition(
    string Name,
    string Description,
    string Type,
    bool Required,
    string DefaultValue,
    IReadOnlyList<string> Options);

internal sealed record WorkflowDispatchDefinition(IReadOnlyList<WorkflowInputDefinition> Inputs)
{
    private static readonly Regex PropertyLine = new(@"^(?<indent> *)(?<key>[^:#][^:]*):(?:\s*(?<value>.*))?$", RegexOptions.CultureInvariant);
    private static readonly Regex InputName = new(@"^[A-Za-z_][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

    internal static WorkflowDispatchDefinition Parse(string content)
    {
        if (content.Contains('\t'))
        {
            throw Unsupported();
        }

        var lines = content.Split('\n');
        var onLine = Array.FindIndex(lines, line => IsProperty(line, 0, "on", out _));
        var dispatchLine = -1;
        if (onLine >= 0)
        {
            for (var i = onLine + 1; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var indent = Indent(lines[i]);
                if (indent == 0)
                {
                    break;
                }

                if (indent == 2 && IsProperty(lines[i], indent, "workflow_dispatch", out _))
                {
                    dispatchLine = i;
                    break;
                }
            }
        }

        if (dispatchLine < 0)
        {
            throw new GitHubApiException("This workflow doesn't define workflow_dispatch, so it can't be run from Command Palette.");
        }

        var dispatchIndent = Indent(lines[dispatchLine]);
        if (!IsProperty(lines[dispatchLine], dispatchIndent, "workflow_dispatch", out var dispatchValue)
            || dispatchValue.Length > 0)
        {
            throw Unsupported();
        }

        var inputsLine = -1;
        for (var i = dispatchLine + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = Indent(lines[i]);
            if (indent <= dispatchIndent)
            {
                break;
            }

            if (indent == dispatchIndent + 2 && IsProperty(lines[i], indent, "inputs", out var value))
            {
                if (value.Length > 0)
                {
                    throw Unsupported();
                }
                inputsLine = i;
                break;
            }
        }

        if (inputsLine < 0)
        {
            return new WorkflowDispatchDefinition([]);
        }

        var inputs = new List<WorkflowInputDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var currentName = string.Empty;
        var description = string.Empty;
        var type = "string";
        var required = false;
        string? defaultValue = null;
        var options = new List<string>();
        var inputIndent = -1;

        void SaveInput()
        {
            if (currentName.Length == 0)
            {
                return;
            }

            if (!seen.Add(currentName) || (type == "choice" && options.Count == 0))
            {
                throw Unsupported();
            }

            if (defaultValue is null)
            {
                defaultValue = type == "boolean" ? "false"
                    : type == "choice" ? options[0]
                    : type == "number" ? "0"
                    : string.Empty;
            }

            if (type == "choice" && !options.Contains(defaultValue, StringComparer.Ordinal))
            {
                throw Unsupported();
            }

            if (options.Count > 100)
            {
                throw new GitHubApiException("This workflow has too many choice values to show here. Open it on GitHub to run it.");
            }

            inputs.Add(new WorkflowInputDefinition(currentName, description, type, required, defaultValue, options.ToArray()));
        }

        for (var i = inputsLine + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = Indent(lines[i]);
            if (indent <= dispatchIndent + 2)
            {
                break;
            }

            if (indent == dispatchIndent + 4 && IsProperty(lines[i], indent, out var name, out var value))
            {
                SaveInput();
                if (!InputName.IsMatch(name) || value.Length > 0)
                {
                    throw Unsupported();
                }

                currentName = name;
                description = string.Empty;
                type = "string";
                required = false;
                defaultValue = null;
                options = [];
                inputIndent = indent;
                continue;
            }

            if (currentName.Length == 0 || indent < inputIndent + 2)
            {
                throw Unsupported();
            }

            if (type == "choice" && indent >= inputIndent + 4 && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                options.Add(ParseScalar(trimmed[2..]));
                continue;
            }

            if (IsProperty(lines[i], indent, out var property, out value))
            {
                switch (property)
                {
                    case "description":
                        description = ParseScalar(value);
                        break;
                    case "type":
                        type = ParseScalar(value).ToLowerInvariant();
                        if (type is not ("string" or "choice" or "boolean" or "number" or "environment"))
                        {
                            throw Unsupported();
                        }
                        break;
                    case "required":
                        if (!bool.TryParse(ParseScalar(value), out required))
                        {
                            throw Unsupported();
                        }
                        break;
                    case "default":
                        defaultValue = ParseScalar(value);
                        break;
                    case "options":
                        if (value.Length > 0)
                        {
                            throw Unsupported();
                        }
                        type = "choice";
                        break;
                    default:
                        throw Unsupported();
                }
            }
            else if (!(type == "choice" && trimmed.StartsWith("- ", StringComparison.Ordinal)))
            {
                throw Unsupported();
            }
        }

        SaveInput();
        if (inputs.Count > 10)
        {
            throw new GitHubApiException("This workflow has more than 10 dispatch inputs. Open it on GitHub to run it.");
        }

        return new WorkflowDispatchDefinition(inputs);
    }

    internal Dictionary<string, string> Validate(IReadOnlyDictionary<string, string?> supplied)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalLength = 0;
        foreach (var input in Inputs)
        {
            var value = supplied.TryGetValue(input.Name, out var entered)
                ? entered ?? string.Empty
                : input.DefaultValue;
            totalLength += input.Name.Length + value.Length;
            if (totalLength > 65_535)
            {
                throw new GitHubApiException("The workflow inputs exceed GitHub's dispatch size limit.");
            }

            if (input.Required && string.IsNullOrWhiteSpace(value))
            {
                throw new GitHubApiException($"Enter a value for the required input '{input.Name}'.");
            }

            if (input.Type == "choice" && !input.Options.Contains(value, StringComparer.Ordinal))
            {
                throw new GitHubApiException($"Choose one of the listed values for '{input.Name}'.");
            }

            var booleanValue = false;
            if (input.Type == "boolean" && !bool.TryParse(value, out booleanValue))
            {
                throw new GitHubApiException($"Choose true or false for '{input.Name}'.");
            }

            if (input.Type == "number"
                && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    || !double.IsFinite(number)))
            {
                throw new GitHubApiException($"Enter a number for '{input.Name}'.");
            }

            result[input.Name] = input.Type == "boolean"
                ? (booleanValue ? "true" : "false")
                : value;
        }

        foreach (var suppliedName in supplied.Keys)
        {
            if (!Inputs.Any(input => input.Name == suppliedName))
            {
                throw new GitHubApiException($"'{suppliedName}' isn't an input for this workflow.");
            }
        }

        return result;
    }

    internal static string DecodeContent(string encoded)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            throw new GitHubApiException("GitHub returned workflow content that couldn't be decoded.", ex);
        }
    }

    private static bool IsProperty(string line, int indent, string name, out string value) =>
        IsProperty(line, indent, out var actualName, out value) && actualName == name;

    private static bool IsProperty(string line, int indent, out string name, out string value)
    {
        var match = PropertyLine.Match(line);
        if (!match.Success || match.Groups["indent"].Length != indent)
        {
            name = string.Empty;
            value = string.Empty;
            return false;
        }

        name = Unquote(match.Groups["key"].Value.Trim());
        value = match.Groups["value"].Value.Trim();
        return name.Length > 0;
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    private static string ParseScalar(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value.StartsWith('#'))
        {
            return string.Empty;
        }

        if (value.StartsWith('[') || value.StartsWith('{') || value.StartsWith('&') || value.StartsWith('*')
            || value.StartsWith('!') || value.StartsWith('|') || value.StartsWith('>'))
        {
            throw Unsupported();
        }

        if (value[0] is '"' or '\'')
        {
            var quote = value[0];
            for (var i = 1; i < value.Length; i++)
            {
                if (quote == '"' && value[i] == '\\')
                {
                    throw Unsupported();
                }

                if (value[i] == quote)
                {
                    if (quote == '\'' && i + 1 < value.Length && value[i + 1] == '\'')
                    {
                        throw Unsupported();
                    }

                    var remainder = value[(i + 1)..].TrimStart();
                    if (remainder.Length == 0
                        || (char.IsWhiteSpace(value[i + 1]) && remainder.StartsWith('#')))
                    {
                        return value[1..i];
                    }

                    throw Unsupported();
                }
            }

            throw Unsupported();
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] == '#' && char.IsWhiteSpace(value[i - 1]))
            {
                return value[..i].TrimEnd();
            }
        }

        return value;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    private static GitHubApiException Unsupported() =>
        new("This workflow uses a dispatch input format Command Palette can't validate yet. Open it on GitHub to run it.");
}

internal sealed record GitHubWorkflowJob(
    long Id,
    string Name,
    string Status,
    string? Conclusion,
    Uri? HtmlUrl,
    IReadOnlyList<GitHubWorkflowStep> Steps);

internal sealed record GitHubWorkflowStep(string Name, string Status, string? Conclusion, int Number);

internal sealed record WorkflowJobsPageResult(IReadOnlyList<GitHubWorkflowJob> Jobs, Uri? NextPage);

internal sealed record GitHubArtifact(long Id, string Name, long SizeInBytes, bool Expired);

internal sealed record WorkflowArtifactsPageResult(IReadOnlyList<GitHubArtifact> Artifacts, Uri? NextPage);
