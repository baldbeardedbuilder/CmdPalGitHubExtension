// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;

namespace BaldBeardedBuilder.CmdPal.GitHub.Actions;

internal sealed partial class WorkflowRunDetails : Details
{
    public WorkflowRunDetails(string repository, GitHubWorkflowRun run)
    {
        Title = run.DisplayTitle;
        Body = run.Name;

        var metadata = new List<IDetailsElement>
        {
            new DetailsElement
            {
                Key = "Status",
                Data = new DetailsTags { Tags = [new Tag(WorkflowRunFormatting.State(run))] },
            },
        };
        AddText(metadata, "Repository", repository);
        AddText(metadata, "Actor", run.Actor.Length > 0 ? $"@{run.Actor}" : null);
        AddText(metadata, "Event", run.Event);
        AddText(metadata, "Branch", run.HeadBranch);
        AddText(metadata, "Run number", run.RunNumber?.ToString(CultureInfo.CurrentCulture));
        AddText(metadata, "Attempt", run.RunAttempt?.ToString(CultureInfo.CurrentCulture));
        AddDate(metadata, "Started", run.CreatedAt);
        AddDate(metadata, "Updated", run.UpdatedAt);
        AddText(metadata, "Commit", run.HeadSha);
        metadata.Add(new DetailsElement
        {
            Key = "Workflow run",
            Data = new DetailsLink { Text = $"#{run.RunNumber ?? run.Id}", Link = run.WebUrl },
        });
        Metadata = [.. metadata];
    }

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
}
