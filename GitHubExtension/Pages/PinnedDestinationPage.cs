using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal partial class PinnedDestinationPage : ListPage, IDisposable
{
    private readonly AuthService _auth;
    private readonly AuthService _pageAuth;
    private readonly PinDestination _destination;
    private readonly Func<AuthService, ListPage> _factory;
    private readonly Lock _lock = new();
    private readonly ListItem _signInItem;
    private ListPage? _page;
    private GitHubAccount? _pageAccount;
    private bool _disposed;

    internal PinnedDestinationPage(AuthService auth, PinDestination destination, SignInPage signIn, Func<AuthService, ListPage> factory)
    {
        (_auth, _destination, _factory) = (auth, destination, factory);
        _pageAuth = destination.IsRepository ? auth.ForAccount(destination.Host!, destination.Login!) : auth;
        Id = destination.Id;
        Name = "Open";
        Title = destination.Title;
        Icon = destination.Icon;
        _signInItem = new ListItem(signIn)
        {
            Title = destination.IsRepository ? $"Sign in as @{destination.Login} on {destination.Host!.Name}" : "Sign in to GitHub",
            Subtitle = destination.IsRepository ? $"This pin opens {destination.Repository} with its original account." : $"Sign in to open {destination.Title}.",
            Icon = Icons.Account,
        };
        _auth.AccountChanged += AccountChanged;
    }

    internal ListPage? LoadedPage
    {
        get { lock (_lock) { return _page; } }
    }

    public override IListItem[] GetItems()
    {
        ListPage? page;
        lock (_lock)
        {
            if (_disposed) { return []; }
            page = _destination.Matches(_auth.CurrentAccount) ? _page ??= CreatePage() : null;
        }

        if (page is null)
        {
            EmptyContent = _signInItem;
            return UnavailableItems();
        }

        if (!IsCurrent(page)) { return UnavailableItems(); }
        if (page is DynamicListPage dynamicPage && dynamicPage.SearchText != SearchText) { dynamicPage.SearchText = SearchText; }
        var items = page.GetItems();
        if (!IsCurrent(page)) { return UnavailableItems(); }
        Publish(page);
        if (!IsCurrent(page)) { return UnavailableItems(); }
        return items;
    }

    private ListPage CreatePage()
    {
        _pageAccount = _auth.CurrentAccount;
        var page = _factory(_pageAuth);
        page.ItemsChanged += PageItemsChanged;
        page.PropChanged += PagePropChanged;
        return page;
    }

    private void Publish(ListPage page)
    {
        if (IsCurrent(page)) { IsLoading = page.IsLoading; }
        if (IsCurrent(page)) { HasMoreItems = page.HasMoreItems; }
        if (IsCurrent(page)) { ShowDetails = page.ShowDetails; }
        if (IsCurrent(page)) { Filters = page.Filters; }
        if (IsCurrent(page)) { PlaceholderText = page.PlaceholderText; }
        if (IsCurrent(page)) { EmptyContent = page.EmptyContent; }
    }

    protected void UpdateSearchText(string oldSearch, string newSearch)
    {
        lock (_lock) { if (_disposed) { return; } }
        if (newSearch.Length == 0)
        {
            RetirePage();
            IsLoading = false;
            HasMoreItems = false;
            Filters = null;
        }

        ListPage? page;
        lock (_lock) { page = _disposed ? null : _page; }
        if (_destination.Matches(_auth.CurrentAccount) && page is DynamicListPage dynamicPage)
        {
            dynamicPage.SearchText = newSearch;
        }
        RaiseItemsChanged();
    }

    public override void LoadMore()
    {
        ListPage? page;
        lock (_lock) { page = _disposed ? null : _page; }
        if (_destination.Matches(_auth.CurrentAccount)) { page?.LoadMore(); }
    }

    private void PageItemsChanged(object sender, IItemsChangedEventArgs args)
    {
        if (IsCurrent(sender)) { RaiseItemsChanged(); }
    }

    private void PagePropChanged(object sender, IPropChangedEventArgs args)
    {
        if (sender is ListPage page && IsCurrent(page)) { Publish(page); }
    }

    private bool IsCurrent(object sender)
    {
        lock (_lock)
        {
            return !_disposed && ReferenceEquals(sender, _page) && _pageAccount == _auth.CurrentAccount
                && _destination.Matches(_pageAccount);
        }
    }

    private void AccountChanged(object? sender, EventArgs e)
    {
        lock (_lock) { if (_disposed) { return; } }
        RetirePage();
        IsLoading = false;
        HasMoreItems = false;
        Filters = null;
        SearchText = "";
        EmptyContent = _signInItem;
        RaiseItemsChanged();
    }

    private void RetirePage()
    {
        ListPage? page;
        lock (_lock)
        {
            page = _page;
            _page = null;
            _pageAccount = null;
        }
        if (page is null) { return; }
        page.ItemsChanged -= PageItemsChanged;
        page.PropChanged -= PagePropChanged;
        (page as IDisposable)?.Dispose();
    }

    private IListItem[] UnavailableItems()
    {
        lock (_lock) { return _disposed ? [] : [_signInItem]; }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) { return; }
            _disposed = true;
        }
        _auth.AccountChanged -= AccountChanged;
        RetirePage();
        if (!ReferenceEquals(_pageAuth, _auth)) { _pageAuth.Dispose(); }
        IsLoading = false;
        HasMoreItems = false;
        Filters = null;
    }
}

internal sealed partial class PinnedDynamicDestinationPage(
    AuthService auth,
    PinDestination destination,
    SignInPage signIn,
    Func<AuthService, ListPage> factory)
    : PinnedDestinationPage(auth, destination, signIn, factory), IDynamicListPage
{
    public override string SearchText
    {
        get => base.SearchText;
        set
        {
            var previousSearch = base.SearchText;
            SetSearchNoUpdate(value);
            UpdateSearchText(previousSearch, value);
        }
    }
}
