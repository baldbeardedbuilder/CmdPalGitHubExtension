using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;

namespace BaldBeardedBuilder.CmdPal.GitHub.Search;

internal sealed record SavedIssueQuery(string Name, string Query, IssueSearchKind Kind);

internal interface ISavedIssueQueryStore
{
    IReadOnlyList<SavedIssueQuery> Load(GitHubAccount account);
    void Save(GitHubAccount account, SavedIssueQuery query);
    void Delete(GitHubAccount account, string name);
}

[JsonSerializable(typeof(SavedIssueQuery[]))]
internal sealed partial class SavedIssueQueriesJsonContext : JsonSerializerContext;

internal sealed class SavedIssueQueryStore(string? directory = null) : ISavedIssueQueryStore
{
    private static readonly Lock Sync = new();
    private readonly string _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BaldBeardedBuilder", "CmdPalGitHub", "searches");

    internal string AccountPath(GitHubAccount account)
    {
        var identity = $"{account.Host.WebUrl.AbsoluteUri.ToLowerInvariant()}\n{account.Login.ToUpperInvariant()}";
        return Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }

    public IReadOnlyList<SavedIssueQuery> Load(GitHubAccount account)
    {
        lock (Sync)
        {
            var path = AccountPath(account);
            if (!File.Exists(path)) { return []; }
            try
            {
                return (JsonSerializer.Deserialize(File.ReadAllText(path), SavedIssueQueriesJsonContext.Default.SavedIssueQueryArray) ?? [])
                    .Where(q => !string.IsNullOrWhiteSpace(q.Name) && !string.IsNullOrWhiteSpace(q.Query) && Enum.IsDefined(q.Kind)).ToArray();
            }
            catch (JsonException)
            {
                throw new GitHubApiException("Your saved query file couldn't be read. Restore it before saving another query.");
            }
        }
    }

    public void Save(GitHubAccount account, SavedIssueQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Name)) { throw new GitHubApiException("Give this saved query a name."); }
        _ = IssueSearchClient.ScopeQuery(query.Query, query.Kind);
        Change(account, existing => [.. existing.Where(q => !q.Name.Equals(query.Name, StringComparison.OrdinalIgnoreCase)), query]);
    }

    public void Delete(GitHubAccount account, string name) =>
        Change(account, existing => [.. existing.Where(q => !q.Name.Equals(name, StringComparison.OrdinalIgnoreCase))]);

    private void Change(GitHubAccount account, Func<IReadOnlyList<SavedIssueQuery>, SavedIssueQuery[]> change)
    {
        lock (Sync)
        {
            var values = change(Load(account));
            Directory.CreateDirectory(_directory);
            var path = AccountPath(account);
            var staging = path + ".new";
            try
            {
                File.WriteAllText(staging, JsonSerializer.Serialize(values, SavedIssueQueriesJsonContext.Default.SavedIssueQueryArray));
                File.Move(staging, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(staging)) { File.Delete(staging); }
            }
        }
    }
}
