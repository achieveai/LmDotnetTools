using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace Reviewd.Sample;

/// <summary>
///     The exact ADO paths a read-only-egress session may still POST to so that prepare can run in it: the
///     repository's git fetch and prepare's two context queries (a work-item batch and a commit-to-PR query). Each is
///     a read sent as a POST because it carries a body; the policy builder refuses any other path.
/// </summary>
internal static partial class AdoReadOnlyPaths
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex Organization();

    /// <summary>
    ///     The paths for <paramref name="repo" />, from ADO's own repository record (<c>GET
    ///     _apis/git/repositories/{repo}</c>). They match what prepare sends: its git fetch URL lowercases the
    ///     organization and re-escapes the project and repository names from <c>remoteUrl</c>; its REST client uses
    ///     the organization as given. Both spellings are granted.
    /// </summary>
    internal static IReadOnlyList<string> For(RepoIdentity repo, JsonElement repository)
    {
        var org = repo.OrgOrOwner;
        if (!Organization().IsMatch(org))
        {
            throw new ArgumentException($"'{org}' is not an Azure DevOps organization name.");
        }
        var repositoryId = Required(repository, "id");
        var projectId = repository.TryGetProperty("project", out var project) ? Required(project, "id") : null;
        var remote = Uri.TryCreate(Required(repository, "remoteUrl"), UriKind.Absolute, out var url) ? url : null;
        var parts = remote?.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray() ?? [];
        // dev.azure.com/{org}/{project}/_git/{repo}, or the legacy {org}.visualstudio.com/{project}/_git/{repo}.
        var (gitProject, gitRepository) = parts switch
        {
            [_, var p, "_git", var r] when remote!.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) => (
                p,
                r
            ),
            [.., var p, "_git", var r]
                when remote!.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) => (p, r),
            _ => throw new InvalidOperationException(
                $"ADO returned an unexpected remoteUrl for {repo.DisplayName}; no read-only session was created."
            ),
        };
        if (projectId is null)
        {
            throw new InvalidOperationException(
                $"ADO's record for {repo.DisplayName} has no project id; no read-only session was created."
            );
        }
        var paths = new List<string>();
        foreach (var o in new[] { org, org.ToLowerInvariant() }.Distinct(StringComparer.Ordinal))
        {
            paths.Add(
                $"/{o}/{Uri.EscapeDataString(gitProject)}/_git/{Uri.EscapeDataString(gitRepository)}/git-upload-pack"
            );
            paths.Add($"/{o}/_apis/wit/workitemsbatch");
            paths.Add(
                $"/{o}/{Uri.EscapeDataString(projectId)}/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/pullrequestquery"
            );
        }
        return paths;
    }

    /// <summary>Reads <paramref name="repo" />'s repository record from ADO with the host's signed-in token.</summary>
    internal static async Task<IReadOnlyList<string>> ResolveAsync(
        HttpClient http,
        IOAuthTokenProvider tokens,
        RepoIdentity repo,
        CancellationToken ct
    )
    {
        var url =
            $"https://dev.azure.com/{Uri.EscapeDataString(repo.OrgOrOwner)}/{Uri.EscapeDataString(repo.Project!)}/_apis/git/repositories/{Uri.EscapeDataString(repo.RepoName)}?api-version=7.1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var token = await tokens.GetAccessTokenAsync(ct: ct);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"))
        );
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ADO refused the repository read for {repo.DisplayName} ({(int)response.StatusCode}); no read-only session was created."
            );
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return For(repo, document.RootElement);
    }

    private static string Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidOperationException($"ADO's repository record has no '{name}'.");
}
