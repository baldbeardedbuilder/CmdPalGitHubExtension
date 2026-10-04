// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal static class MutationConfirmation
{
    public static string ResultCard(string message, Uri? authorizeUrl) => $$"""
        {
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "type": "AdaptiveCard", "version": "1.6",
          "body": [
            { "type": "TextBlock", "text": {{GitHubJson.String(message)}}, "wrap": true }
            {{(authorizeUrl is null ? string.Empty : $$"""
            , { "type": "ActionSet", "actions": [
              { "type": "Action.OpenUrl", "title": "Authorize organization access", "url": {{GitHubJson.String(authorizeUrl.AbsoluteUri)}} }
            ] }
            """)}}
          ]
        }
        """;

    public static string Card(GitHubAccount account, string action, string target, string consequences, string submitAction = "confirm") =>
        $$"""
        {
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "type": "AdaptiveCard", "version": "1.6",
          "body": [
            { "type": "TextBlock", "text": {{GitHubJson.String(action)}}, "size": "Large", "weight": "Bolder", "wrap": true },
            { "type": "TextBlock", "text": {{GitHubJson.String($"Account: {account.Login}\nHost: {account.Host.WebUrl}\nTarget: {target}")}}, "wrap": true },
            { "type": "TextBlock", "text": {{GitHubJson.String(consequences)}}, "wrap": true },
            { "type": "ActionSet", "actions": [
              { "type": "Action.Submit", "title": "Confirm", "data": { "action": {{GitHubJson.String(submitAction)}} } },
              { "type": "Action.Submit", "title": "Cancel", "data": { "action": "cancel" } }
            ] }
          ]
        }
        """;
}

internal sealed partial class MutationConfirmationPage : ContentPage
{
    private readonly ConfirmationForm _form;
    internal Task CurrentSubmission { get; private set; } = Task.CompletedTask;

    public MutationConfirmationPage(GitHubAccount account, string action, string target, string consequences,
        Func<Task> submit, Func<(string Message, Uri? AuthorizeUrl)> feedback, Func<bool> isCurrent)
    {
        Name = action;
        Title = action;
        _form = new ConfirmationForm(this, MutationConfirmation.Card(account, action, target, consequences), submit, feedback, isCurrent);
    }

    public override IContent[] GetContent() => [_form];

    private sealed partial class ConfirmationForm : FormContent
    {
        private readonly Func<Task> _submit;
        private readonly MutationConfirmationPage _page;
        private readonly Func<(string Message, Uri? AuthorizeUrl)> _feedback;
        private readonly Func<bool> _isCurrent;
        private bool _submitted;
        public ConfirmationForm(MutationConfirmationPage page, string template, Func<Task> submit,
            Func<(string Message, Uri? AuthorizeUrl)> feedback, Func<bool> isCurrent)
        {
            _page = page;
            TemplateJson = template;
            _submit = submit;
            _feedback = feedback;
            _isCurrent = isCurrent;
        }
        public override ICommandResult SubmitForm(string inputs, string data)
        {
            string? action;
            try
            {
                using var json = JsonDocument.Parse(data);
                action = json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("action", out var value)
                    && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            }
            catch (JsonException)
            {
                return CommandResult.KeepOpen();
            }

            if (action is not ("confirm" or "cancel"))
            {
                return CommandResult.KeepOpen();
            }

            if (Interlocked.Exchange(ref _submitted, true))
            {
                return CommandResult.KeepOpen();
            }

            if (action == "cancel")
            {
                TemplateJson = MutationConfirmation.ResultCard("Cancelled. Return to the list to continue.", null);
                _page.RaiseItemsChanged();
                return CommandResult.KeepOpen();
            }

            _page.CurrentSubmission = RunAsync();
            return CommandResult.KeepOpen();
        }

        private async Task RunAsync()
        {
            _page.IsLoading = true;
            try
            {
                if (!_isCurrent())
                {
                    ShowCancelled();
                    return;
                }

                await _submit().ConfigureAwait(false);
                if (!_isCurrent())
                {
                    ShowCancelled();
                    return;
                }

                var feedback = _feedback();
                TemplateJson = MutationConfirmation.ResultCard(feedback.Message, feedback.AuthorizeUrl);
            }
            catch (Exception)
            {
                if (!_isCurrent())
                {
                    ShowCancelled();
                }
                else
                {
                    TemplateJson = MutationConfirmation.ResultCard("Couldn't complete this request. Return to the list and refresh to check GitHub before retrying.", null);
                }
            }
            finally
            {
                _page.IsLoading = false;
                _page.RaiseItemsChanged();
            }
        }

        private void ShowCancelled() =>
            TemplateJson = MutationConfirmation.ResultCard("Cancelled because the account changed or the originating page was closed. Return to the list and review the action again.", null);

    }
}
