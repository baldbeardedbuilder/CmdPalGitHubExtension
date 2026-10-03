using System.Text.Json;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class BrowsingFormPage : ContentPage
{
    private readonly BrowsingForm _form;

    internal BrowsingFormPage(string name, string body, Func<JsonElement, string?> submit)
    {
        Name = name;
        Title = name;
        Id = $"com.baldbeardedbuilder.cmdpal.github.query.{Guid.NewGuid():N}";
        _form = new(this, body, submit);
    }

    public override IContent[] GetContent() => [_form];

    internal static string Text(string id, string label, string value = "") =>
        $$"""{ "type":"Input.Text", "id":{{GitHubJson.String(id)}}, "label":{{GitHubJson.String(label)}}, "value":{{GitHubJson.String(value)}} }""";

    internal static string Choice(string id, string label, string value, params (string Title, string Value)[] choices) =>
        $$"""{ "type":"Input.ChoiceSet", "id":{{GitHubJson.String(id)}}, "label":{{GitHubJson.String(label)}}, "value":{{GitHubJson.String(value)}}, "choices":[{{string.Join(',', choices.Select(c => $$"""{"title":{{GitHubJson.String(c.Title)}},"value":{{GitHubJson.String(c.Value)}}}"""))}}] }""";

    private sealed partial class BrowsingForm : FormContent
    {
        private readonly BrowsingFormPage _page;
        private readonly string _body;
        private readonly Func<JsonElement, string?> _submit;
        internal BrowsingForm(BrowsingFormPage page, string body, Func<JsonElement, string?> submit)
        {
            (_page, _body, _submit) = (page, body, submit);
            TemplateJson = Card(body, null);
        }
        public override ICommandResult SubmitForm(string inputs, string data)
        {
            string? error;
            try
            {
                using var json = JsonDocument.Parse(inputs);
                if (json.RootElement.ValueKind != JsonValueKind.Object) { return CommandResult.KeepOpen(); }
                error = _submit(json.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or GitHubApiException or IOException or UnauthorizedAccessException or FormatException)
            {
                error = ex is GitHubApiException ? ex.Message : "Couldn't apply these settings. Check your inputs and local storage.";
            }

            TemplateJson = Card(_body, error ?? "Applied. Return to the list to see your results.");
            _page.RaiseItemsChanged();
            return CommandResult.KeepOpen();
        }

        private static string Card(string body, string? message) => $$"""
            {"type":"AdaptiveCard","version":"1.6","body":[{{body}}
            {{(message is null ? "" : $$""",{"type":"TextBlock","text":{{GitHubJson.String(message)}},"wrap":true}""")}}
            ],"actions":[{"type":"Action.Submit","title":"Apply"}]}
            """;
    }
}
