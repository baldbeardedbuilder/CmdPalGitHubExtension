// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

internal sealed partial class PullRequestDetails : Details
{
    public PullRequestDetails(GitHubPullRequest pullRequest)
    {
        Title = $"#{pullRequest.Number} {pullRequest.Title}";
        Body = string.IsNullOrWhiteSpace(pullRequest.Body) ? "No description provided." : pullRequest.Body;

        var metadata = new List<IDetailsElement>();
        if (NotificationFormatting.StateTag("PullRequest", pullRequest.State) is { } state)
        {
            metadata.Add(new DetailsElement { Key = "State", Data = new DetailsTags { Tags = [state] } });
        }

        AddText(metadata, "Repository", pullRequest.RepositoryFullName);
        AddText(metadata, "Author", pullRequest.Author is { Length: > 0 } author ? $"@{author}" : null);
        AddText(metadata, "From", pullRequest.HeadBranch);
        AddText(metadata, "Into", pullRequest.BaseBranch);
        if (pullRequest.Labels.Length > 0)
        {
            metadata.Add(new DetailsElement
            {
                Key = "Labels",
                Data = new DetailsTags { Tags = [.. pullRequest.Labels.Select(label => new Tag(label))] },
            });
        }

        AddDate(metadata, "Created", pullRequest.CreatedAt);
        AddDate(metadata, "Updated", pullRequest.UpdatedAt);
        AddCount(metadata, "Commits", pullRequest.Commits);
        AddCount(metadata, "Changed files", pullRequest.ChangedFiles);
        AddCount(metadata, "Additions", pullRequest.Additions);
        AddCount(metadata, "Deletions", pullRequest.Deletions);
        metadata.Add(new DetailsElement
        {
            Key = "Pull request",
            Data = new DetailsLink { Text = $"#{pullRequest.Number}", Link = pullRequest.WebUrl },
        });
        Metadata = [.. metadata];
    }

    private PullRequestDetails(string title, string message)
    {
        Title = title;
        Body = message;
    }

    public static PullRequestDetails Loading(string title) => new(title, "Loading pull request details...");

    public static PullRequestDetails Unavailable(string title, string message) => new(title, message);

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

    private static void AddCount(List<IDetailsElement> metadata, string key, int? count)
    {
        if (count is { } value)
        {
            AddText(metadata, key, value.ToString("N0", CultureInfo.CurrentCulture));
        }
    }
}
