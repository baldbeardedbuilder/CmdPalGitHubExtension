// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class ForceCancelWorkflowRunPage : ContentPage
{
    private readonly MutationConfirmationPage _confirmation;

    public ForceCancelWorkflowRunPage(ActionsPage page, WorkflowRunItem item)
    {
        Id = $"{ActionsPage.PageId}.force-cancel.{item.Run.Id}";
        Name = "Force cancel";
        Title = "Force cancel workflow run?";
        Icon = Icons.Stop;
        _confirmation = new MutationConfirmationPage(item.Account!, "Force cancel workflow run",
            $"{item.Repository}, run {item.Run.Id}",
            "Use this only when normal cancellation is stuck. GitHub may stop the run without completing its cleanup.",
            () => page.ForceCancelAsync(item), page.CancellationFeedback,
            () => page.IsCancellationContextCurrent(item) && page.CanForceCancel(item.Run.Id));
    }

    internal Task CurrentSubmission => _confirmation.CurrentSubmission;

    public override IContent[] GetContent() => _confirmation.GetContent();
}
