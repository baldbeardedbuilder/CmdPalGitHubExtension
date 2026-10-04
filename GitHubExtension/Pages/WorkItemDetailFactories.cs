using BaldBeardedBuilder.CmdPal.GitHub.Auth;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed record WorkItemDetailFactories(
    Func<GitHubAccount, string, int, Func<bool>, ICommand?>? PullRequestDetails = null,
    Func<GitHubAccount, string, int, bool, Func<bool>, ICommand?>? Conversation = null);

internal sealed partial class WorkItemDetailsCache(WorkItemDetailFactories? factories) : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ICommand[]> _pages = [];

    internal IContextItem[] Commands(string key, GitHubAccount account, string repository, int number,
        bool pullRequest, Func<bool> isCurrent, IconInfo? icon = null)
    {
        if (factories is null || !isCurrent()) { return []; }
        ICommand[] pages;
        lock (_lock)
        {
            if (_pages.TryGetValue(key, out var cached))
            {
                pages = cached;
            }
            else
            {
                pages = new[]
                {
                    pullRequest ? factories.PullRequestDetails?.Invoke(account, repository, number, isCurrent) : null,
                    factories.Conversation?.Invoke(account, repository, number, pullRequest, isCurrent),
                }.OfType<ICommand>().ToArray();
                _pages[key] = pages;
            }

        }
        if (icon is not null)
        {
            foreach (var conversation in pages.OfType<IssueConversationPage>()) conversation.Icon = icon;
        }
        return pages.Select(page => (IContextItem)new CommandContextItem(page)).ToArray();
    }

    public void Dispose()
    {
        ICommand[] pages;
        lock (_lock)
        {
            pages = _pages.Values.SelectMany(value => value).Distinct().ToArray();
            _pages.Clear();
        }

        foreach (var page in pages.OfType<IDisposable>()) { page.Dispose(); }
    }
}
