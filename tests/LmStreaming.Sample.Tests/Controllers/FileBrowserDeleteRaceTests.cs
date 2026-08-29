using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Pins the #213 window the delete endpoint CLOSES: an entry substituted between the authoritative
/// resolve (directory listing) and the removal must be refused at the point of use, not acted on with the
/// removal the RESOLVED kind earned. The race is expressed by the fixture, never by the clock — the
/// listing the controller resolves against says one kind while the real filesystem the command runs
/// against already holds the substituted object, which is exactly the state a lost race leaves behind. A
/// wall-clock version of this test would be won too reliably to fail when the guard is deleted.
/// </summary>
/// <remarks>
/// These tests execute the controller's REAL delete script under a REAL POSIX <c>sh</c> against a real
/// temp directory, because the claim under test is about what the script DOES — that its kind re-check
/// runs in the same invocation as the <c>rm</c> and refuses a substituted object. A recording fake can
/// only assert the argv (FileBrowserControllerTests does); it cannot fail when the script text itself is
/// wrong. What stays out of scope here, matching the V1 boundary: the check→<c>rm</c> window INSIDE the
/// script and path-COMPONENT substitutions, both of which need a gateway-side atomic primitive.
/// </remarks>
public sealed class FileBrowserDeleteRaceTests
{
    private const string ThreadId = "t-race";

    /// <summary>
    /// The seam that makes the lost race a fixture instead of a timing: listings (what the resolve
    /// believed) are scripted, while commands (what the delete does) run against the real directory —
    /// so the two can disagree exactly the way a concurrent substitution makes them disagree.
    /// </summary>
    private sealed class StaleListingBrowser(string root, string shell) : IWorkspaceFileBrowser
    {
        private readonly LocalShellWorkspaceBrowser _shell = new(root, shell);

        public Dictionary<string, IReadOnlyList<SandboxDirectoryEntry>> Listings { get; } = new(StringComparer.Ordinal);

        public Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionAsync(
            string threadId,
            string persistedWorkspaceId,
            SandboxCredential? requestCredential,
            CancellationToken ct = default
        ) => _shell.ResolveThreadWorkspaceSessionAsync(threadId, persistedWorkspaceId, requestCredential, ct);

        public Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionForBackgroundAsync(
            string threadId,
            string persistedWorkspaceId,
            CancellationToken ct = default
        ) => _shell.ResolveThreadWorkspaceSessionForBackgroundAsync(threadId, persistedWorkspaceId, ct);

        public Task<IReadOnlyList<SandboxDirectoryEntry>> ListWorkspaceDirectoryAsync(
            string sessionId,
            string relativePath,
            CancellationToken ct = default
        ) =>
            Task.FromResult<IReadOnlyList<SandboxDirectoryEntry>>(
                Listings.TryGetValue(relativePath, out var entries) ? entries : []
            );

        public Task<byte[]> ReadWorkspaceFileBytesAsync(
            string sessionId,
            string relativePath,
            long? maxBytes,
            CancellationToken ct = default
        ) => _shell.ReadWorkspaceFileBytesAsync(sessionId, relativePath, maxBytes, ct);

        public Task WriteWorkspaceFileBytesAsync(
            string sessionId,
            string relativePath,
            byte[] bytes,
            CancellationToken ct = default
        ) => _shell.WriteWorkspaceFileBytesAsync(sessionId, relativePath, bytes, ct);

        public Task<SandboxCommandResult> ExecuteWorkspaceCommandAsync(
            string sessionId,
            SandboxCommand command,
            CancellationToken ct = default
        ) => _shell.ExecuteWorkspaceCommandAsync(sessionId, command, ct);
    }

    private static (FileBrowserController Controller, StaleListingBrowser Browser, string Root) Build(string shell)
    {
        var root = Path.Combine(Path.GetTempPath(), "fb-delete-race-" + Guid.NewGuid().ToString("N")[..8]);
        _ = Directory.CreateDirectory(root);

        var store = new Mock<IConversationStore>();
        store
            .Setup(s => s.LoadMetadataAsync(ThreadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new ThreadMetadata
                {
                    ThreadId = ThreadId,
                    LastUpdated = 0,
                    Properties = ImmutableDictionary<string, object>.Empty.Add(
                        MultiTurnAgentPool.WorkspacePropertyKey,
                        "ws-race"
                    ),
                }
            );

        var browser = new StaleListingBrowser(root, shell);
        var controller = new FileBrowserController(
            store.Object,
            browser,
            TestAuthorizers.Disabled(),
            NullLogger<FileBrowserController>.Instance
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            },
        };
        return (controller, browser, root);
    }

    private static string RequireShell()
    {
        var shell = LocalShellWorkspaceBrowser.FindPosixShell();
        Skip.If(shell is null, "No POSIX shell (sh) on this machine, so the real delete script cannot run.");
        return shell;
    }

    private static void AssertEntryChanged(IActionResult result)
    {
        var conflict = result.Should().BeOfType<ConflictObjectResult>().Which;
        // entry_changed specifically — a generic 409 (target_busy et al.) would hide WHICH refusal fired.
        JsonSerializer.Serialize(conflict.Value).Should().Contain("entry_changed");
    }

    [SkippableFact]
    public async Task Delete_DirectoryResolved_ButFileSubstituted_RefusesAndTheFileSurvives()
    {
        var shell = RequireShell();
        var (controller, browser, root) = Build(shell);
        try
        {
            // The resolve saw a directory; by the time the removal runs, the entry is a regular FILE.
            // The stale-typed code path chose `rm -r -- sub` here and destroyed the substituted file.
            browser.Listings[""] = [new SandboxDirectoryEntry("sub", SandboxEntryType.Directory, null, false)];
            var victim = Path.Combine(root, "sub");
            await File.WriteAllTextAsync(victim, "substituted payload");

            var result = await controller.Delete(ThreadId, "sub", CancellationToken.None);

            AssertEntryChanged(result);
            File.Exists(victim).Should().BeTrue("the point-of-use kind check must refuse before any rm runs");
            (await File.ReadAllTextAsync(victim)).Should().Be("substituted payload");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Delete_FileResolved_ButDirectorySubstituted_RefusesWith409_NotAConfusing422()
    {
        var shell = RequireShell();
        var (controller, browser, root) = Build(shell);
        try
        {
            // The resolve saw a file; the entry is now a directory with content. A bare `rm --` would
            // merely fail (422 delete_failed) — the guard instead names what happened: the entry changed.
            browser.Listings[""] = [new SandboxDirectoryEntry("sub", SandboxEntryType.File, 4, false)];
            var dir = Path.Combine(root, "sub");
            _ = Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "child.txt"), "kept");

            var result = await controller.Delete(ThreadId, "sub", CancellationToken.None);

            AssertEntryChanged(result);
            File.Exists(Path.Combine(dir, "child.txt")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Delete_DirectoryResolved_StillADirectory_RemovesItRecursively()
    {
        var shell = RequireShell();
        var (controller, browser, root) = Build(shell);
        try
        {
            // Positive control: the same harness, no substitution — proving the guard PASSES a truthful
            // kind and the rm actually runs, so the two refusal tests cannot be green vacuously (a broken
            // script that refuses everything would fail here).
            browser.Listings[""] = [new SandboxDirectoryEntry("sub", SandboxEntryType.Directory, null, false)];
            var dir = Path.Combine(root, "sub");
            _ = Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "child.txt"), "gone");

            var result = await controller.Delete(ThreadId, "sub", CancellationToken.None);

            result.Should().BeOfType<NoContentResult>();
            Directory.Exists(dir).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Delete_FileResolved_ButSymlinkSubstituted_RefusesBeforeClassifyingByTarget()
    {
        var shell = RequireShell();
        var (controller, browser, root) = Build(shell);
        try
        {
            // The f arm's own symlink qualifier, pinned separately from the d arm's: `[ -f link-to-file ]`
            // is TRUE (test follows links), so a bare -f check would bless a substituted link and the rm
            // would act on it. `[ ! -L ]` must run first on THIS arm too — a link is a change of kind,
            // never classified by its target.
            var victim = Path.Combine(root, "victim.txt");
            await File.WriteAllTextAsync(victim, "kept");

            var link = Path.Combine(root, "sub");
            try
            {
                _ = File.CreateSymbolicLink(link, victim);
            }
            catch (IOException)
            {
                Skip.If(true, "This machine cannot create file symlinks (no privilege).");
            }
            catch (UnauthorizedAccessException)
            {
                Skip.If(true, "This machine cannot create file symlinks (no privilege).");
            }

            browser.Listings[""] = [new SandboxDirectoryEntry("sub", SandboxEntryType.File, 4, false)];

            var result = await controller.Delete(ThreadId, "sub", CancellationToken.None);

            AssertEntryChanged(result);
            File.Exists(link).Should().BeTrue("the substituted link must be left alone");
            (await File.ReadAllTextAsync(victim)).Should().Be("kept");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Delete_DirectoryResolved_ButSymlinkSubstituted_RefusesBeforeClassifyingByTarget()
    {
        var shell = RequireShell();
        var (controller, browser, root) = Build(shell);
        try
        {
            // The sharp variant: the substituted entry is a symlink TO a directory, so a bare `[ -d ]`
            // (which follows links) would still say "directory". The script's `[ ! -L ]` runs FIRST —
            // a substituted link is a change of kind, never classified by what it points at.
            var victimDir = Path.Combine(root, "victim");
            _ = Directory.CreateDirectory(victimDir);
            await File.WriteAllTextAsync(Path.Combine(victimDir, "keep.txt"), "kept");

            var link = Path.Combine(root, "sub");
            try
            {
                _ = Directory.CreateSymbolicLink(link, victimDir);
            }
            catch (IOException)
            {
                Skip.If(true, "This machine cannot create directory symlinks (no privilege).");
            }
            catch (UnauthorizedAccessException)
            {
                Skip.If(true, "This machine cannot create directory symlinks (no privilege).");
            }

            browser.Listings[""] = [new SandboxDirectoryEntry("sub", SandboxEntryType.Directory, null, false)];

            var result = await controller.Delete(ThreadId, "sub", CancellationToken.None);

            AssertEntryChanged(result);
            Directory.Exists(Path.Combine(root, "sub")).Should().BeTrue("the substituted link must be left alone");
            File.Exists(Path.Combine(victimDir, "keep.txt")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
