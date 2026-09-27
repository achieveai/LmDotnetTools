using System.Text.Json;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>Exact checkout and admitted commit identities retained with the trusted workflow preparation.</summary>
internal sealed record PreparedCheckout(
    string StoreRoot,
    string TargetDir,
    string NotesDir,
    string Branch,
    MergeBaseOutcome MergeBase = MergeBaseOutcome.Resolved,
    string? MergeBaseSha = null,
    string? CheckoutSha = null,
    string? SourceHeadSha = null,
    string? TargetBaseSha = null,
    string? PreparedManifest = null,
    bool ContextWarning = false
);

/// <summary>
/// Acquires and verifies outer review repositories. PR checkout/submodule preparation belongs to the
/// installed SourcePool scripts; acquiring a repository never repairs or resets an existing checkout.
/// </summary>
internal sealed class ReviewSlotPreparer
{
    private readonly GitRunner _git;
    private readonly ISandboxFileSystem _fileSystem;

    public ReviewSlotPreparer(GitRunner git, ISandboxFileSystem fileSystem)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    // Directory types require lstat on the remote mount; the file API's name-only listing cannot
    // distinguish a reserved metadata directory from a symlink. This probe never reads or changes files.
    internal const string InspectRootScript = """
        import json, stat
        from pathlib import Path
        def kind(path):
            mode = path.lstat().st_mode
            return 'directory' if stat.S_ISDIR(mode) else 'file' if stat.S_ISREG(mode) else 'other'
        entries = list(Path.cwd().iterdir())
        print(json.dumps({
            'hasGitDirectory': any(p.name == '.git' and kind(p) == 'directory' for p in entries),
            'pristine': all(p.name == '.mcp-gateway' and kind(p) == 'directory' for p in entries),
            'safeReservedPaths': all(kind(p) == 'directory' for p in entries if p.name in ('.git', '.mcp-gateway'))
        }))
        """;

    private sealed record RootInspection(
        [property: System.Text.Json.Serialization.JsonRequired] bool HasGitDirectory,
        [property: System.Text.Json.Serialization.JsonRequired] bool Pristine,
        [property: System.Text.Json.Serialization.JsonRequired] bool SafeReservedPaths
    );

    public async Task EnsureWorkspaceRootAsync(string origin, CancellationToken ct)
    {
        if (!SameStoreOrigin(origin, origin))
            throw new InvalidOperationException("The review store must have an unambiguous HTTP(S) origin.");
        ct.ThrowIfCancellationRequested();
        var probe = await _git
            .CommandRunner.RunAsync(new SandboxCommand(["python3", "-c", InspectRootScript], "/workspace"), ct)
            .ConfigureAwait(false);
        RequireSuccess(probe, "Workspace classification");
        var root =
            JsonSerializer.Deserialize<RootInspection>(
                probe.Stdout,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            ) ?? throw new InvalidDataException("Workspace classification was empty.");
        if (!root.SafeReservedPaths)
            throw new InvalidOperationException("Reserved workspace paths must be ordinary directories.");
        if (root.HasGitDirectory)
        {
            await VerifyWorkspaceRootAsync(origin, ct).ConfigureAwait(false);
            return;
        }
        if (!root.Pristine)
            throw new InvalidOperationException("The workspace contains user content; reconciliation is required.");

        // The caller's durable bootstrap intent is already written. Once init starts, an unacknowledged
        // operation quarantines the workspace; a definite partial failure is inspected, never auto-repaired.
        await BootstrapGitAsync(["init", "--template=", "."], ct).ConfigureAwait(false);
        await BootstrapGitAsync(["remote", "add", "origin", origin], ct).ConfigureAwait(false);
        await VerifyStoreOriginAsync(_git, "/workspace", origin, ct).ConfigureAwait(false);
        var advertised = await BootstrapGitAsync(["ls-remote", "--symref", "origin", "HEAD"], ct).ConfigureAwait(false);
        var branch = RemoteDefaultBranch(advertised.Stdout);
        await BootstrapGitAsync(["check-ref-format", "--branch", branch], ct).ConfigureAwait(false);
        await BootstrapGitAsync(["fetch", "--tags", "origin"], ct).ConfigureAwait(false);
        var remoteBranch = "refs/remotes/origin/" + branch;
        var tree = await BootstrapGitAsync(["ls-tree", "--name-only", remoteBranch], ct).ConfigureAwait(false);
        if (tree.Stdout.Split('\n').Contains(".mcp-gateway", StringComparer.Ordinal))
            throw new InvalidOperationException(
                "The remote tree collides with Gateway metadata; reconciliation is required."
            );
        ct.ThrowIfCancellationRequested();
        try
        {
            await _fileSystem
                .WriteFileAsync("/workspace/.git/info/exclude", "/.mcp-gateway/\n", ct)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new RemoteWorkspaceOutcomeUnknownException(error);
        }
        await BootstrapGitAsync(["checkout", "-b", branch, "--track", remoteBranch], ct).ConfigureAwait(false);
        await VerifyWorkspaceRootAsync(origin, ct).ConfigureAwait(false);
    }

    private async Task VerifyWorkspaceRootAsync(string origin, CancellationToken ct)
    {
        var top = await _git.RunAsync(["rev-parse", "--show-toplevel"], "/workspace", ct).ConfigureAwait(false);
        var head = await _git.RunAsync(["rev-parse", "--verify", "HEAD^{commit}"], "/workspace", ct)
            .ConfigureAwait(false);
        if (!top.Succeeded || top.Stdout.TrimEnd('\r', '\n', '/') != "/workspace" || !head.Succeeded)
            throw new InvalidOperationException(
                "The outer repository is incomplete or not the workspace root; reconciliation is required."
            );
        await VerifyStoreOriginAsync(_git, "/workspace", origin, ct).ConfigureAwait(false);
        var index = await _git.RunAsync(
                ["diff", "--cached", "--ignore-submodules=none", "--quiet", "HEAD", "--"],
                "/workspace",
                ct
            )
            .ConfigureAwait(false);
        RequireSuccess(index, "Outer index cleanliness");
        var tracked = await _git.RunAsync(
                ["diff", "--raw", "--no-abbrev", "--no-renames", "--ignore-submodules=none", "--"],
                "/workspace",
                ct
            )
            .ConfigureAwait(false);
        RequireSuccess(tracked, "Outer tracked-file inspection");
        if (string.IsNullOrWhiteSpace(tracked.Stdout))
            return;
        var modules = await _git.RunAsync(
                ["config", "--file", ".gitmodules", "--get-regexp", "^submodule\\..*\\.path$"],
                "/workspace",
                ct
            )
            .ConfigureAwait(false);
        RequireSuccess(modules, "Declared root module inspection");
        var paths = modules
            .Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line[(line.IndexOf(' ') + 1)..].TrimEnd('\r'))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var line in tracked.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            var fields = parts[0].Split(' ');
            if (
                parts.Length != 2
                || fields.Length != 5
                || fields[0] != ":160000"
                || fields[1] != "160000"
                || fields[4] != "M"
                || !paths.Contains(parts[1])
            )
                throw new InvalidOperationException(
                    "The outer checkout has modified ordinary tracked files; reconciliation is required."
                );
        }
        // Repo-setup subsequently verifies each root module's cleanliness and owned source store.
        // Only the recorded gitlink SHA may differ; no ordinary file or staged change is exempted.
    }

    private async Task<SandboxCommandResult> BootstrapGitAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SandboxCommandResult result;
        try
        {
            result = await _git.RunAsync(args, "/workspace", ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new RemoteWorkspaceOutcomeUnknownException(error);
        }
        RequireSuccess(result, "Outer repository " + args[0]);
        return result;
    }

    private static string RemoteDefaultBranch(string output)
    {
        const string prefix = "ref: refs/heads/";
        var refs = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line =>
                line.StartsWith(prefix, StringComparison.Ordinal) && line.EndsWith("\tHEAD", StringComparison.Ordinal)
            )
            .Select(line => line[prefix.Length..^5])
            .ToArray();
        if (refs.Length != 1)
            throw new InvalidOperationException("The remote default branch is unavailable or ambiguous.");
        return refs[0];
    }

    private static void RequireSuccess(SandboxCommandResult result, string operation)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"{operation} failed with exit {result.ExitCode}; reconciliation may be required."
            );
    }

    public async Task EnsureStoreAsync(string storeRoot, string storeUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeUrl);
        var probe = await _git.RunAsync(["-C", storeRoot, "rev-parse", "--git-dir"], storeRoot, cancellationToken)
            .ConfigureAwait(false);
        if (probe.Succeeded)
        {
            await VerifyStoreOriginAsync(_git, storeRoot, storeUrl, cancellationToken).ConfigureAwait(false);
            return;
        }
        if ((await _fileSystem.ListFilesAsync(storeRoot, cancellationToken).ConfigureAwait(false)).Count != 0)
            throw new InvalidOperationException("The retention checkout is non-empty but is not a Git repository.");
        var clone = await _git.RunAsync(["clone", storeUrl, storeRoot], null, cancellationToken).ConfigureAwait(false);
        if (!clone.Succeeded)
            throw new InvalidOperationException($"Retention clone failed with exit {clone.ExitCode}.");
    }

    public static async Task VerifyStoreOriginAsync(
        GitRunner git,
        string storeRoot,
        string expectedUrl,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedUrl);
        foreach (var push in new[] { false, true })
        {
            string[] arguments = push
                ? ["-C", storeRoot, "remote", "get-url", "--push", "--all", "origin"]
                : ["-C", storeRoot, "remote", "get-url", "--all", "origin"];
            var result = await git.RunAsync(arguments, storeRoot, cancellationToken).ConfigureAwait(false);
            var urls = result.Stdout.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            if (!result.Succeeded || urls.Length != 1 || !SameStoreOrigin(expectedUrl, urls[0]))
                throw new InvalidOperationException(
                    "Review store origin does not match the configured repository; refusing to reuse it."
                );
        }
    }

    private static bool SameStoreOrigin(string expected, string actual)
    {
        if (
            !Uri.TryCreate(expected, UriKind.Absolute, out var expectedUri)
            || !Uri.TryCreate(actual, UriKind.Absolute, out var actualUri)
            || expectedUri.Scheme is not ("https" or "http")
            || actualUri.Scheme != expectedUri.Scheme
            || expectedUri.Port != actualUri.Port
            || expectedUri.Query.Length != 0
            || actualUri.Query.Length != 0
            || expectedUri.Fragment.Length != 0
            || actualUri.Fragment.Length != 0
        )
            return false;
        var left = GitRemoteUrl.CanonicalizeAdoLegacyHost(GitRemoteUrl.Parse(OriginSpelling(expectedUri)));
        var right = GitRemoteUrl.CanonicalizeAdoLegacyHost(GitRemoteUrl.Parse(OriginSpelling(actualUri)));
        var comparison =
            left.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || left.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        return left.Kind == right.Kind
            && left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase)
            && left.RepoPath.Equals(right.RepoPath, comparison);
    }

    private static string OriginSpelling(Uri uri) =>
        uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            .Replace("@", "%40", StringComparison.Ordinal)
            .TrimEnd('/');
}
