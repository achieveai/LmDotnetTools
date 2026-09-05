using System.Security.Cryptography;
using System.Text;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// The daemon's HOST-side write surface for review-store retention (design §6). Bundles the host git runner,
/// filesystem, isolated checkout path, and expected remote, so all retention writes happen in the daemon process
/// with the write credential — never in the read-only sandbox the review agent shares.
/// </summary>
internal sealed record HostRetentionWorkspace(
    ISandboxCommandRunner Git,
    ISandboxFileSystem FileSystem,
    string RepoRoot,
    string StoreUrl
)
{
    public static string ResolveRoot(string? hostRoot, string appId, string storeUrl)
    {
        if (!string.IsNullOrWhiteSpace(hostRoot))
        {
            return Path.Combine(hostRoot, "review-store-retention");
        }

        var parsed = GitRemoteUrl.Parse(storeUrl);
        var identity =
            Uri.TryCreate(storeUrl, UriKind.Absolute, out var remote) && !string.IsNullOrEmpty(remote.Host)
                ? $"{remote.Scheme}://{remote.Host}:{remote.Port}{GitRemoteUrl.Parse(remote.GetLeftPart(UriPartial.Path)).RepoPath}"
            : string.IsNullOrEmpty(parsed.Host) ? storeUrl
            : $"{parsed.Kind}://{parsed.Host.ToLowerInvariant()}{parsed.RepoPath}";
        return Path.Combine(
            AppContext.BaseDirectory,
            "workspaces",
            SandboxAppDir.Derive(appId),
            "review-store-retention-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16]
        );
    }
}
