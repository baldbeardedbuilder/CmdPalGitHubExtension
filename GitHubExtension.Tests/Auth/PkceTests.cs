// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Auth;

[TestClass]
public class PkceTests
{
    [TestMethod]
    public void CreateChallenge_MatchesRfc7636Example()
    {
        // Appendix B of RFC 7636.
        Assert.AreEqual(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [TestMethod]
    public void CreateRandomString_IsUrlSafeAndUnique()
    {
        var first = Pkce.CreateRandomString();
        var second = Pkce.CreateRandomString();

        Assert.AreNotEqual(first, second);
        Assert.AreEqual(43, first.Length);
        Assert.IsTrue(first.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }
}
