// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Auth;

[TestClass]
public class GitHubHostTests
{
    [TestMethod]
    [DataRow("github.com")]
    [DataRow("https://github.com")]
    [DataRow("  https://GitHub.com/some/path  ")]
    public void TryParse_GitHubDotCom_ReturnsSharedInstance(string input)
    {
        Assert.IsTrue(GitHubHost.TryParse(input, out var host));
        Assert.AreSame(GitHubHost.GitHubDotCom, host);
        Assert.IsTrue(host.IsGitHubDotCom);
        Assert.AreEqual(new Uri("https://api.github.com/"), host.ApiUrl);
    }

    [TestMethod]
    [DataRow("github.example.com", "https://github.example.com/", "https://github.example.com/api/v3/")]
    [DataRow("https://GitHub.Example.com/orgs/foo", "https://github.example.com/", "https://github.example.com/api/v3/")]
    [DataRow("https://github.example.com:8443", "https://github.example.com:8443/", "https://github.example.com:8443/api/v3/")]
    [DataRow("octocorp.ghe.com", "https://octocorp.ghe.com/", "https://api.octocorp.ghe.com/")]
    public void TryParse_EnterpriseHosts_NormalizesUrls(string input, string web, string api)
    {
        Assert.IsTrue(GitHubHost.TryParse(input, out var host));
        Assert.AreEqual(new Uri(web), host.WebUrl);
        Assert.AreEqual(new Uri(api), host.ApiUrl);
        Assert.IsFalse(host.IsGitHubDotCom);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("http://github.example.com")]
    [DataRow("ftp://github.example.com")]
    [DataRow("https://user:pass@github.example.com")]
    [DataRow("https://")]
    public void TryParse_InvalidInput_ReturnsFalse(string? input)
    {
        Assert.IsFalse(GitHubHost.TryParse(input, out var host));
        Assert.IsNull(host);
    }

    [TestMethod]
    public void Equality_IsBasedOnWebUrl()
    {
        Assert.IsTrue(GitHubHost.TryParse("github.example.com", out var a));
        Assert.IsTrue(GitHubHost.TryParse("https://GITHUB.example.com/", out var b));
        Assert.AreEqual(a, b);
        Assert.AreEqual(a!.GetHashCode(), b!.GetHashCode());
    }
}
