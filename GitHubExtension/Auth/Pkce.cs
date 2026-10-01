// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace BaldBeardedBuilder.CmdPal.GitHub.Auth;

internal static class Pkce
{
    public static string CreateRandomString(int byteCount = 32) => Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

    public static string CreateChallenge(string verifier) => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
