// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Issues;

internal sealed partial class IssueDetails : Details
{
    public IssueDetails(GitHubIssue issue, string repository)
    {
        Title = $"#{issue.Number} {issue.Title}";
        Body = string.IsNullOrWhiteSpace(issue.Body) ? "No description provided." : issue.Body;

        var metadata = new List<IDetailsElement>();
        if (NotificationFormatting.StateTag("Issue", issue.State) is { } state)
        {
            metadata.Add(new DetailsElement { Key = "State", Data = new DetailsTags { Tags = [state] } });
        }

        AddText(metadata, "Repository", repository);
        AddText(metadata, "Author", issue.Author is { Length: > 0 } author ? $"@{author}" : null);
        if (issue.Assignees.Count > 0)
        {
            metadata.Add(new DetailsElement
            {
                Key = "Assignees",
                Data = new DetailsTags { Tags = [.. issue.Assignees.Select(name => new Tag($"@{name}"))] },
            });
        }

        if (issue.Labels.Count > 0)
        {
            metadata.Add(new DetailsElement
            {
                Key = "Labels",
                Data = new DetailsTags { Tags = [.. issue.Labels.Select(label => new Tag(label))] },
            });
        }

        AddDate(metadata, "Created", issue.CreatedAt);
        AddCount(metadata, "Comments", issue.Comments);
        metadata.Add(new DetailsElement
        {
            Key = "Issue",
            Data = new DetailsLink { Text = $"#{issue.Number}", Link = issue.WebUrl },
        });
        Metadata = [.. metadata];
    }

    private IssueDetails(string title, string message, Uri? authorizeUrl = null)
    {
        Title = title;
        Body = message;
        if (authorizeUrl is not null)
        {
            Metadata =
            [
                new DetailsElement
                {
                    Key = "Single sign-on",
                    Data = new DetailsLink { Text = "Authorize on GitHub", Link = authorizeUrl },
                },
            ];
        }
    }

    public static IssueDetails Loading(string title) => new(title, "Loading issue details...");

    public static IssueDetails Unavailable(string title, string message, Uri? authorizeUrl = null) => new(title, message, authorizeUrl);

    private static void AddText(List<IDetailsElement> metadata, string key, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            metadata.Add(new DetailsElement { Key = key, Data = new DetailsLink { Text = text } });
        }
    }

    private static void AddDate(List<IDetailsElement> metadata, string key, DateTimeOffset value)
    {
        if (value != DateTimeOffset.MinValue)
        {
            AddText(metadata, key, value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        }
    }

    private static void AddCount(List<IDetailsElement> metadata, string key, int count) =>
        AddText(metadata, key, count.ToString("N0", CultureInfo.CurrentCulture));
}
