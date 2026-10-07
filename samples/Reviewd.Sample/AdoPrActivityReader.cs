using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace Reviewd.Sample;

internal sealed class AdoPrActivityReader(HttpClient http, IOAuthTokenProvider tokens)
{
    internal sealed record Activity(DateTimeOffset? SourceUpdatedAt, DateTimeOffset? LastCommentPostedAt);

    public async Task<Activity> ReadAsync(RepoIdentity repo, string prId, CancellationToken ct)
    {
        var root =
            $"https://dev.azure.com/{Uri.EscapeDataString(repo.OrgOrOwner)}/{Uri.EscapeDataString(repo.Project!)}/_apis/git/repositories/{Uri.EscapeDataString(repo.RepoName)}";
        using var pr = await GetAsync($"{root}/pullrequests/{prId}?api-version=7.1", ct);
        DateTimeOffset? sourceUpdated = null;
        // A fork's source ref belongs to another repository; never report the target's same-named branch.
        if (!pr.RootElement.TryGetProperty("forkSource", out var fork) || fork.ValueKind == JsonValueKind.Null)
        {
            if (
                pr.RootElement.TryGetProperty("sourceRefName", out var source)
                && source.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(source.GetString())
            )
            {
                using var pushes = await GetAsync(
                    $"{root}/pushes?searchCriteria.refName={Uri.EscapeDataString(source.GetString()!)}&$top=1&api-version=7.1",
                    ct
                );
                foreach (var push in Values(pushes).EnumerateArray())
                {
                    sourceUpdated = Later(sourceUpdated, Timestamp(push, "date"));
                }
            }
        }
        using var threads = await GetAsync($"{root}/pullrequests/{prId}/threads?api-version=7.1", ct);
        DateTimeOffset? posted = null;
        foreach (var thread in Values(threads).EnumerateArray())
        {
            if (Deleted(thread))
            {
                continue;
            }
            if (!thread.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("ADO thread comments are malformed; no snapshot was saved.");
            }
            foreach (var comment in comments.EnumerateArray())
            {
                if (
                    !Deleted(comment)
                    && comment.TryGetProperty("commentType", out var type)
                    && (
                        (type.ValueKind == JsonValueKind.String && type.GetString() == "text")
                        || (type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var number) && number == 1)
                    )
                )
                {
                    posted = Later(posted, Timestamp(comment, "publishedDate"));
                }
            }
        }
        return new Activity(sourceUpdated, posted);
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url).WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        var token = await tokens.GetAccessTokenAsync(ct: ct);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"))
        );
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        if (
            url.Contains("/threads?", StringComparison.Ordinal)
            && (
                (
                    response.Headers.TryGetValues("x-ms-continuationtoken", out var continuation)
                    && continuation.Any(value => !string.IsNullOrWhiteSpace(value))
                )
                || (
                    response.Headers.TryGetValues("Link", out var links)
                    && links.Any(value => value.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
                )
            )
        )
        {
            throw new InvalidOperationException("ADO threads listing is incomplete; no snapshot was saved.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static JsonElement Values(JsonDocument document)
    {
        var root = document.RootElement;
        if (
            !root.TryGetProperty("value", out var values)
            || values.ValueKind != JsonValueKind.Array
            || (
                root.TryGetProperty("count", out var count)
                && (!count.TryGetInt32(out var size) || size != values.GetArrayLength())
            )
        )
        {
            throw new InvalidOperationException(
                "ADO activity listing is malformed or incomplete; no snapshot was saved."
            );
        }
        return values;
    }

    private static bool Deleted(JsonElement item) =>
        item.TryGetProperty("isDeleted", out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Timestamp(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (
            value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp
            )
        )
        {
            throw new InvalidOperationException($"ADO {property} timestamp is malformed; no snapshot was saved.");
        }
        return timestamp.ToUniversalTime();
    }

    private static DateTimeOffset? Later(DateTimeOffset? left, DateTimeOffset? right) =>
        right is not null && (left is null || right > left) ? right : left;
}
