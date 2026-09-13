using System.Security.Cryptography;
using System.Text;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// The daemon's HOST-side write surface for ReviewBot retention (design §6). Bundles the host git runner
/// + filesystem + the host path the ReviewBot store is cloned to, so all retention writes happen in the
/// daemon process with the write credential — never in the read-only sandbox the review agent shares.
/// </summary>
internal sealed record HostRetentionWorkspace(ISandboxCommandRunner Git, ISandboxFileSystem FileSystem, string RepoRoot)
{
    /// <summary>Stable across parent/CLI working directories and isolated by configured store destination.</summary>
    internal static string ResolveRoot(string? configuredRoot, string storeUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeUrl);
        var hostRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(AppContext.BaseDirectory, "workspaces")
            : Path.GetFullPath(configuredRoot, AppContext.BaseDirectory);
        var destination = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(storeUrl)));
        return Path.Combine(hostRoot, "retention-" + destination);
    }

    /// <summary>
    /// Acquires the host repository across daemon and CLI processes, including read-only Git operations.
    /// The stable sibling file survives checkout/clone/clean; never delete it when releasing the handle.
    /// Callers must not nest acquisition or hold this lease while awaiting model work.
    /// </summary>
    public static async Task<FileStream> AcquireRepositoryLockAsync(
        string repoRoot,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));
        var parent =
            Path.GetDirectoryName(root)
            ?? throw new ArgumentException("Retention repository must have a parent directory.", nameof(repoRoot));
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(parent);
        var lockPath = root + ".retention.lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous
                );
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
            {
                // EAGAIN / sharing violation / lock violation. Other I/O faults are not contention.
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
