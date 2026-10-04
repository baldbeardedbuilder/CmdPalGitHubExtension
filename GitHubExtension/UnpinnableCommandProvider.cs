// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Windows.Foundation;

namespace BaldBeardedBuilder.CmdPal.GitHub;

// The host treats ICommandProvider4 as pinning support, even when GetCommandItem returns null.
internal sealed partial class UnpinnableCommandProvider(GitHubCommandsProvider provider) : ICommandProvider3
{
    public string Id => provider.Id;
    public string DisplayName => provider.DisplayName;
    public IIconInfo Icon => provider.Icon;
    public ICommandSettings? Settings => provider.Settings;
    public bool Frozen => provider.Frozen;

    public event TypedEventHandler<object, IItemsChangedEventArgs> ItemsChanged
    {
        add => provider.ItemsChanged += value;
        remove => provider.ItemsChanged -= value;
    }

    public ICommandItem[] TopLevelCommands() => provider.TopLevelCommands();
    public IFallbackCommandItem[]? FallbackCommands() => provider.FallbackCommands();
    public ICommand? GetCommand(string id) => provider.GetCommand(id);
    public void InitializeWithHost(IExtensionHost host) => provider.InitializeWithHost(host);
    public object[] GetApiExtensionStubs() => provider.GetApiExtensionStubs();
    public ICommandItem[]? GetDockBands() => provider.GetDockBands();
    public void Dispose() => provider.Dispose();
}
