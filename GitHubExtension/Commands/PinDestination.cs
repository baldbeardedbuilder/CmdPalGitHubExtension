using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Commands;

internal enum PinDestinationKind
{
    Home,
    Notifications,
    Repos,
    Agents,
    Codespaces,
    RepositoryIssues,
    RepositoryPullRequests,
    RepositoryAgents,
    RepositoryActions,
}

internal sealed record PinDestination(PinDestinationKind Kind, GitHubHost? Host = null, string? Login = null, string? Repository = null)
{
    internal const string Prefix = "com.baldbeardedbuilder.cmdpal.github.pin.v1.";
    internal const string DockPrefix = "com.baldbeardedbuilder.cmdpal.github.dock.";

    internal bool IsRepository => Kind >= PinDestinationKind.RepositoryIssues;

    internal string Id => Kind == PinDestinationKind.Home ? HomePage.PageId : Prefix + Kind + (IsRepository
        ? $"|{Uri.EscapeDataString(Host!.WebUrl.AbsoluteUri)}|{Uri.EscapeDataString(Login!.ToLowerInvariant())}|{Uri.EscapeDataString(Repository!.ToLowerInvariant())}"
        : "");

    internal string Title => Kind switch
    {
        PinDestinationKind.Home => "GitHub",
        PinDestinationKind.RepositoryIssues => $"{Repository} Issues",
        PinDestinationKind.RepositoryPullRequests => $"{Repository} Pull Requests",
        PinDestinationKind.RepositoryAgents => $"{Repository} Agents",
        PinDestinationKind.RepositoryActions => $"{Repository} Actions",
        _ => Kind.ToString(),
    };

    internal IconInfo Icon => Kind switch
    {
        PinDestinationKind.Home => Icons.GitHub,
        PinDestinationKind.Notifications => Icons.Notifications,
        PinDestinationKind.Repos => Icons.Repos,
        PinDestinationKind.Agents or PinDestinationKind.RepositoryAgents => Icons.Agents,
        PinDestinationKind.Codespaces => Icons.Codespaces,
        PinDestinationKind.RepositoryIssues => Icons.Issues,
        PinDestinationKind.RepositoryPullRequests => Icons.PullRequests,
        _ => Icons.Actions,
    };

    internal bool Matches(GitHubAccount? account) => account is not null && (!IsRepository
        || (account.Host == Host && string.Equals(account.Login, Login, StringComparison.OrdinalIgnoreCase)));

    internal static string GlobalId(PinDestinationKind kind) => new PinDestination(kind).Id;

    internal static string RepositoryId(PinDestinationKind kind, GitHubAccount? account, string repository) =>
        account is null ? "" : new PinDestination(kind, account.Host, account.Login, repository).Id;

    internal static bool TryParse(string id, out PinDestination? destination)
    {
        destination = null;
        if (id == HomePage.PageId)
        {
            destination = new(PinDestinationKind.Home);
            return true;
        }
        if (!id.StartsWith(Prefix, StringComparison.Ordinal) || id.Length > 2048) { return false; }
        var parts = id[Prefix.Length..].Split('|');
        if (!Enum.TryParse<PinDestinationKind>(parts[0], out var kind) || !Enum.IsDefined(kind)) { return false; }
        if (kind < PinDestinationKind.RepositoryIssues)
        {
            if (parts.Length != 1) { return false; }
            destination = new(kind);
        }
        else
        {
            if (parts.Length != 4) { return false; }
            var hostText = Uri.UnescapeDataString(parts[1]);
            var login = Uri.UnescapeDataString(parts[2]);
            var repository = Uri.UnescapeDataString(parts[3]);
            if (!GitHubHost.TryParse(hostText, out var host) || host.WebUrl.AbsoluteUri != hostText
                || !ValidName(login, owner: true)) { return false; }
            var names = repository.Split('/');
            if (names.Length != 2 || !ValidName(names[0], owner: true) || !ValidName(names[1], owner: false)) { return false; }
            destination = new(kind, host, login, repository);
        }

        if (destination.Id != id)
        {
            destination = null;
            return false;
        }

        return true;
    }

    private static bool ValidName(string name, bool owner) => name.Length is > 0 and <= 100 && name is not "." and not ".."
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || (!owner && c == '.'));
}

internal sealed partial class PinnableListItem : ListItem
{
    internal PinnableListItem(ICommand command) : base(command) => PinItemProperties.Apply(this, command);
}

internal sealed partial class PinnableCommandItem : CommandItem, IExtendedAttributesProvider
{
    internal PinnableCommandItem(ICommand command) : base(command) => PinItemProperties.Apply(this, command);
}

internal sealed partial class PinnableCommandContextItem : CommandContextItem, IExtendedAttributesProvider
{
    internal PinnableCommandContextItem(ICommand command) : base(command) => PinItemProperties.Apply(this, command);
}

internal static class PinItemProperties
{
    internal const string DockCommandId = "Microsoft.CommandPalette.DockCommandId";
    // Dock list pages render their rows as buttons. Resolve a one-row wrapper instead.
    internal static void Apply(CommandItem item, ICommand command)
    {
        if (PinDestination.TryParse(command.Id, out _))
        {
            item.GetProperties()[DockCommandId] = PinDestination.DockPrefix + command.Id;
        }
    }
}
