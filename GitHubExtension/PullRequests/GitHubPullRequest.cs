// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

internal sealed record GitHubPullRequest
{
    public required int Number { get; init; }

    public required string Title { get; init; }

    public required Uri WebUrl { get; init; }

    public required SubjectState State { get; init; }

    public string? Body { get; init; }

    public string? RepositoryFullName { get; init; }

    public string? Author { get; init; }

    public string? HeadBranch { get; init; }

    public string? BaseBranch { get; init; }

    public string[] Labels { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public int? Commits { get; init; }

    public int? ChangedFiles { get; init; }

    public int? Additions { get; init; }

    public int? Deletions { get; init; }

    internal static GitHubPullRequest Parse(JsonElement element, SubjectState state)
    {
        if (GetInt(element, "number") is not > 0
            || GetString(element, "title") is not { } title
            || GetUri(element, "html_url") is not { Scheme: "https" } webUrl)
        {
            throw new GitHubApiException("GitHub sent back a pull request we couldn't read.");
        }

        var head = Object(element, "head");
        var target = Object(element, "base");
        var labels = element.TryGetProperty("labels", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(label => Text(label, "name")).OfType<string>().ToArray()
            : [];

        return new GitHubPullRequest
        {
            Number = GetInt(element, "number"),
            Title = title,
            WebUrl = webUrl,
            State = state,
            Body = GetString(element, "body"),
            RepositoryFullName = Text(Object(target, "repo"), "full_name"),
            Author = Text(Object(element, "user"), "login"),
            HeadBranch = Text(head, "label") ?? Text(head, "ref"),
            BaseBranch = Text(target, "label") ?? Text(target, "ref"),
            Labels = labels,
            CreatedAt = GetDate(element, "created_at"),
            UpdatedAt = GetDate(element, "updated_at"),
            Commits = Count(element, "commits"),
            ChangedFiles = Count(element, "changed_files"),
            Additions = Count(element, "additions"),
            Deletions = Count(element, "deletions"),
        };
    }

    private static JsonElement Object(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object ? value : default;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object ? GetString(element, name) : null;

    private static int? Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var count) ? count : null;
}
