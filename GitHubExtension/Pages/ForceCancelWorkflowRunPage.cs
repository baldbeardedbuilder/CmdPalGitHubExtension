// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class ForceCancelWorkflowRunPage : ContentPage
{
    private readonly ActionsPage _page;
    private readonly WorkflowRunItem _item;

    public ForceCancelWorkflowRunPage(ActionsPage page, WorkflowRunItem item)
    {
        _page = page;
        _item = item;
        Id = $"{ActionsPage.PageId}.force-cancel.{item.Run.Id}";
        Name = "Force cancel";
        Title = "Force cancel workflow run?";
        Icon = Icons.Stop;
    }

    public override IContent[] GetContent() => [new ConfirmationForm(this)];

    private CommandResult Submit(string action)
    {
        if (action == "force")
        {
            _ = _page.ForceCancelAsync(_item);
        }

        return CommandResult.KeepOpen();
    }

    private sealed partial class ConfirmationForm : FormContent
    {
        private readonly ForceCancelWorkflowRunPage _page;

        public ConfirmationForm(ForceCancelWorkflowRunPage page)
        {
            _page = page;
            TemplateJson = $$"""
                {
                    "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                    "type": "AdaptiveCard",
                    "version": "1.6",
                    "body": [
                        { "type": "TextBlock", "text": "Force cancel this workflow run?", "size": "Large", "weight": "Bolder", "wrap": true },
                        { "type": "TextBlock", "text": "Use this only when normal cancellation is stuck. GitHub may stop the run without completing its cleanup.", "wrap": true, "isSubtle": true },
                        { "type": "ActionSet", "spacing": "Large", "actions": [
                            { "type": "Action.Submit", "id": "force", "title": "Force cancel", "style": "destructive", "data": { "action": "force" } },
                            { "type": "Action.Submit", "id": "keep", "title": "Keep run", "associatedInputs": "none", "data": { "action": "keep" } }
                        ] }
                    ]
                }
                """;
        }

        public override ICommandResult SubmitForm(string inputs, string data)
        {
            try
            {
                using var json = JsonDocument.Parse(string.IsNullOrEmpty(data) ? "{}" : data);
                var action = json.RootElement.TryGetProperty("action", out var value) ? value.GetString() : null;
                return _page.Submit(action ?? string.Empty);
            }
            catch (JsonException)
            {
                return CommandResult.KeepOpen();
            }
        }
    }
}
