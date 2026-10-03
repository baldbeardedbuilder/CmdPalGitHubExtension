// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;

namespace BaldBeardedBuilder.CmdPal.GitHub.Api;

internal sealed record GraphQLResult(JsonElement? Data, IReadOnlyList<GraphQLError> Errors)
{
    public bool IsSuccess => Errors.Count == 0;

    public bool HasPartialData => Data is not null && Errors.Count > 0;

    internal static GraphQLResult Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Unreadable();
        }

        JsonElement? data = null;
        if (root.TryGetProperty("data", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw Unreadable();
            }

            data = value.Clone();
        }

        var errors = new List<GraphQLError>();
        if (root.TryGetProperty("errors", out var errorArray))
        {
            if (errorArray.ValueKind != JsonValueKind.Array || errorArray.GetArrayLength() == 0)
            {
                throw Unreadable();
            }

            foreach (var error in errorArray.EnumerateArray())
            {
                if (error.ValueKind != JsonValueKind.Object
                    || GitHubRest.GetString(error, "message") is not { } message)
                {
                    throw Unreadable();
                }

                errors.Add(new GraphQLError(
                    message,
                    CopyProperty(error, "path"),
                    CopyProperty(error, "locations"),
                    CopyProperty(error, "extensions"),
                    GitHubRest.GetString(error, "type")));
            }
        }

        if (data is null && errors.Count == 0)
        {
            throw Unreadable();
        }

        return new GraphQLResult(data, errors);
    }

    private static JsonElement? CopyProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.Clone() : null;

    private static GitHubApiException Unreadable() =>
        new("GitHub sent back a GraphQL response we couldn't read.");
}

internal sealed record GraphQLError(
    string Message,
    JsonElement? Path,
    JsonElement? Locations,
    JsonElement? Extensions,
    string? Type)
{
    public GraphQLField? UnsupportedField =>
        Extensions is { ValueKind: JsonValueKind.Object } extensions
        && GitHubRest.GetString(extensions, "code") == "undefinedField"
        && GitHubRest.GetString(extensions, "typeName") is { } typeName
        && GitHubRest.GetString(extensions, "fieldName") is { } fieldName
            ? new GraphQLField(typeName, fieldName)
            : null;
}

internal sealed record GraphQLField(string TypeName, string FieldName);
