using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task 6 — the provisioner keys a sandbox session by a stable per-run workspace id
/// (<c>review-run-{Id}</c>), so repeated calls for the same run reuse one session instead of creating a
/// new one every stage, and <c>DestroyAsync</c> tears the run's session down at end-of-run.
/// </summary>
public class ReviewSessionProvisionerTests : IDisposable
{
    /// <summary>
    /// Temp root for the tests that exercise the REAL host filesystem — the per-run host workspace teardown
    /// in <c>DestroyAsync</c>. Everything else here runs entirely against the fake session source.
    /// </summary>
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "crd-provisioner-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            // Links first: Directory.Delete's own recursion THROWS on a Windows junction rather than removing
            // it, so a link-planting test would otherwise leave its whole tree in the temp directory. The
            // read-only bits these tests plant would block the delete too.
            DirectoryLink.UnlinkAllUnder(_tempRoot);
            foreach (var file in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only; leaving a stray temp dir must never fail the test.
        }
    }

    private static ReviewRun Run(long id = 7) =>
        new()
        {
            Id = id,
            RepoId = 1,
            PrId = "42",
            HeadSha = "abc1234",
            BaseSha = "def5678",
            TriggerWatermark = "w",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "auto",
            Stage = ReviewStage.ContextReady,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        };

    /// <summary>
    /// Always-sufficient disk probe injected into every test below so provisioning outcomes are decided by
    /// the fake session source alone, never by the real free space of the drive running the test (Task 18's
    /// guard reads the actual host disk by default — see <see cref="GetOrCreateAsync_ReturnsNull_WhenDiskSpaceProbeReportsInsufficientSpace"/>
    /// for a test that exercises that guard deterministically instead).
    /// </summary>
    private static readonly Func<string, bool> SufficientDisk = _ => true;

    [Fact]
    public async Task GetOrCreateAsync_SameRun_ReusesOneSession()
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: "/ws", diskSpaceProbe: SufficientDisk);

        var a = await provisioner.GetOrCreateAsync(Run(), default);
        var b = await provisioner.GetOrCreateAsync(Run(), default);

        a.Should().NotBeNull();
        b.Should().NotBeNull();
        a!.SessionId.Should().Be(b!.SessionId);
        fake.CreateCount.Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreateAsync_ReturnsNull_WhenDiskSpaceProbeReportsInsufficientSpace()
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: "/ws", diskSpaceProbe: _ => false);

        var session = await provisioner.GetOrCreateAsync(Run(), default);

        session.Should().BeNull();
        fake.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task DestroyAsync_TearsDownTheRunSession()
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: "/ws", diskSpaceProbe: SufficientDisk);

        _ = await provisioner.GetOrCreateAsync(Run(), default);
        await provisioner.DestroyAsync(Run(), default);

        fake.DestroyedWorkspaceIds.Should().Contain("review-run-7");
    }

    [Fact]
    public async Task GetOrCreateForSlotAsync_MountsTheSlotRelativeToTheWorkspaceBase()
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: "/ws", diskSpaceProbe: SufficientDisk);
        var slot = new ReviewSlot(
            0, "/ws/review-pool/slot-0", "/ws/review-pool/slot-0/store", "/ws/review-pool/slot-0/scratch");

        var session = await provisioner.GetOrCreateForSlotAsync(Run(), slot, default);

        session.Should().NotBeNull();
        fake.LastRef.Should().NotBeNull();
        // The session is still keyed by the per-run workspace id, but the MOUNTED directory is the slot's
        // host path relative to the base, forward-slashed — so /workspace becomes the slot itself.
        fake.LastRef!.Id.Should().Be("review-run-7");
        fake.LastRef.DirectoryRelPath.Should().Be("review-pool/slot-0");
    }

    [Fact]
    public async Task GetOrCreateRequiredForSlotAsync_MountsTheExactSlot()
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: "/ws", diskSpaceProbe: SufficientDisk);
        var slot = new ReviewSlot(
            0, "/ws/review-pool/slot-0", "/ws/review-pool/slot-0/store", "/ws/review-pool/slot-0/scratch");

        var session = await provisioner.GetOrCreateRequiredForSlotAsync(Run(), slot, default);

        session.SessionId.Should().Be("session-review-run-7");
        fake.LastRef!.DirectoryRelPath.Should().Be("review-pool/slot-0");
    }

    [Theory]
    [InlineData(null, "/ws/review-pool/slot-0")]
    [InlineData("/ws", "/other/slot-0")]
    public async Task GetOrCreateRequiredForSlotAsync_RejectsAnUnrepresentableSlot(
        string? workspaceBase,
        string slotPath)
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: workspaceBase, diskSpaceProbe: SufficientDisk);
        var slot = new ReviewSlot(0, slotPath, $"{slotPath}/store", $"{slotPath}/scratch");

        Func<Task> act = () => provisioner.GetOrCreateRequiredForSlotAsync(Run(), slot, default);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*pooled slot*workspace base*");
        fake.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task GetOrCreateForSlotAsync_FallsBackToPerRunMount_WhenNoWorkspaceBaseConfigured()
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: null, diskSpaceProbe: SufficientDisk);
        var slot = new ReviewSlot(
            0, "/ws/review-pool/slot-0", "/ws/review-pool/slot-0/store", "/ws/review-pool/slot-0/scratch");

        var session = await provisioner.GetOrCreateForSlotAsync(Run(), slot, default);

        session.Should().NotBeNull();
        // No base configured → the slot cannot be expressed under it, so it degrades to the per-run mount:
        // DirectoryRelPath is the review-run-{id} id, NOT the slot leaf.
        fake.LastRef!.DirectoryRelPath.Should().Be("review-run-7");
    }

    [Fact]
    public async Task GetOrCreateForSlotAsync_FallsBackToPerRunMount_WhenSlotEscapesTheWorkspaceBase()
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: "/ws", diskSpaceProbe: SufficientDisk);
        // The slot lives OUTSIDE the configured base, so mounting it at /workspace would escape the base —
        // the provisioner refuses and degrades to the per-run mount rather than throwing.
        var slot = new ReviewSlot(0, "/other/slot-0", "/other/slot-0/store", "/other/slot-0/scratch");

        var session = await provisioner.GetOrCreateForSlotAsync(Run(), slot, default);

        session.Should().NotBeNull();
        fake.LastRef!.DirectoryRelPath.Should().Be("review-run-7");
    }

    /// <summary>
    /// The per-run host workspace holds an UNTRUSTED checkout: the review agent writes into it and takes its
    /// instructions from the reviewed repo's own CLAUDE.md/AGENTS.md as read at the PR head. The teardown's
    /// read-only clear used <c>SearchOption.AllDirectories</c>, which follows a junction without saying so, so
    /// a link planted anywhere under the workspace aimed that clear at an arbitrary path on the daemon host
    /// under the daemon's own account — stripping the last write brake off files outside the workspace.
    /// <para>
    /// The visible failure was never the damage. The recursive delete that follows throws on a Windows
    /// junction, and the whole block was wrapped in a best-effort <c>catch</c> that logged a warning — so the
    /// strip landed, the delete did not, and the only trace was a line that reads like a transient I/O
    /// nuisance. This pins the strip, which is the half that succeeded.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DestroyAsync_does_not_strip_read_only_through_a_link_planted_in_the_host_workspace()
    {
        var hostRoot = Path.Combine(_tempRoot, "workspaces");
        var hostDir = Path.Combine(hostRoot, "review-run-7");
        var outside = Path.Combine(_tempRoot, "outside");
        _ = Directory.CreateDirectory(hostDir);
        _ = Directory.CreateDirectory(outside);
        var protectedFile = Path.Combine(outside, "protected.txt");
        await File.WriteAllTextAsync(protectedFile, "not the daemon's to unlock");
        File.SetAttributes(protectedFile, File.GetAttributes(protectedFile) | FileAttributes.ReadOnly);
        DirectoryLink.Create(Path.Combine(hostDir, "escape"), outside);

        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            fake,
            new CodeReviewDaemonOptions { WorkspaceHostRoot = hostRoot },
            NullLoggerFactory.Instance,
            workspaceBasePath: "/ws",
            diskSpaceProbe: SufficientDisk);

        await provisioner.DestroyAsync(Run(), default);

        File.Exists(protectedFile).Should().BeTrue(
            "the wipe unlinks the NAME inside the workspace and never touches what it points at");
        File.GetAttributes(protectedFile).HasFlag(FileAttributes.ReadOnly).Should().BeTrue(
            "clearing read-only through a planted link removes a write brake from a file outside the "
                + "workspace, on the daemon host, under the daemon's own account");
    }

    /// <summary>
    /// The positive companion to the test above. Its assertions are about an ABSENCE — a bit that stays set,
    /// a file that stays put — and an absence is satisfied by a teardown that walked nothing at all. This
    /// proves the walk is not vacuous: it still reaches, unlocks and deletes a legitimate read-only checkout,
    /// which is the whole reason the read-only clear exists (a git store is full of read-only pack/object
    /// files that <c>Directory.Delete</c> otherwise refuses).
    /// </summary>
    [Fact]
    public async Task DestroyAsync_still_clears_read_only_and_deletes_a_legitimate_host_workspace()
    {
        var hostRoot = Path.Combine(_tempRoot, "workspaces");
        var hostDir = Path.Combine(hostRoot, "review-run-7");
        var nested = Path.Combine(hostDir, "store", ".git", "objects", "pack");
        _ = Directory.CreateDirectory(nested);
        var packFile = Path.Combine(nested, "pack-abc.pack");
        await File.WriteAllTextAsync(packFile, "read-only, exactly as git leaves it");
        File.SetAttributes(packFile, File.GetAttributes(packFile) | FileAttributes.ReadOnly);

        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            fake,
            new CodeReviewDaemonOptions { WorkspaceHostRoot = hostRoot },
            NullLoggerFactory.Instance,
            workspaceBasePath: "/ws",
            diskSpaceProbe: SufficientDisk);

        await provisioner.DestroyAsync(Run(), default);

        Directory.Exists(hostDir).Should().BeFalse(
            "a read-only pack file is the ordinary case the clear exists for, and the teardown must still "
                + "complete over it");
    }

    /// <summary>
    /// A refusal must not arrive in the shape of an ordinary I/O error. "Best-effort host-dir cleanup failed"
    /// is precisely the sentence that would hide a planted link forever — it reads as transient, and an
    /// operator who skims it never learns there is an address to go and look at. So a containment refusal is
    /// logged at Error with the offending entry named, and the best-effort warning is NOT also emitted.
    /// <para>
    /// It is deliberately not rethrown either: this runs from the run's terminal cleanup, where a throw would
    /// abandon the remaining teardown, and unlike the pooled preparer's wipe there is no address to retire —
    /// the next run provisions a fresh <c>review-run-{id}</c> rather than recycling this one. The session
    /// teardown assertion below is what pins that the refusal did not abort the rest of the method.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DestroyAsync_logs_a_containment_refusal_as_an_error_and_leaves_the_workspace_standing()
    {
        var hostRoot = Path.Combine(_tempRoot, "workspaces");
        var outside = Path.Combine(_tempRoot, "outside");
        _ = Directory.CreateDirectory(hostRoot);
        _ = Directory.CreateDirectory(outside);
        var protectedFile = Path.Combine(outside, "protected.txt");
        await File.WriteAllTextAsync(protectedFile, "not the daemon's to delete");
        // The workspace ROOT itself is the link — the one entry a per-child check can never see, because
        // everything below is reached THROUGH it.
        DirectoryLink.Create(Path.Combine(hostRoot, "review-run-7"), outside);

        var fake = new FakeSessionSource();
        var loggerFactory = new CapturingLoggerFactory();
        var provisioner = new ReviewSessionProvisioner(
            fake,
            new CodeReviewDaemonOptions { WorkspaceHostRoot = hostRoot },
            loggerFactory,
            workspaceBasePath: "/ws",
            diskSpaceProbe: SufficientDisk);

        await provisioner.DestroyAsync(Run(), default);

        loggerFactory.Capturing.CountAtLevel(LogLevel.Error, "REFUSED").Should().Be(1);
        loggerFactory.Capturing.CountAtLevel(LogLevel.Warning, "Best-effort host-dir cleanup failed")
            .Should().Be(0, "a security refusal reported as a transient nuisance is how this stays hidden");
        File.Exists(protectedFile).Should().BeTrue("a refused root is not followed AND not removed");
        Directory.Exists(Path.Combine(hostRoot, "review-run-7")).Should().BeTrue(
            "the link is not repaired either — unlinking is a write chosen by whoever planted it");
        fake.DestroyedWorkspaceIds.Should().Contain(
            "review-run-7", "the refusal is logged, not thrown, so the rest of the teardown still ran");
    }

    /// <summary>
    /// The sibling of the test above, on the OTHER refusal this method can meet: not a redirected root the
    /// walk refuses to enter, but a redirected entry inside it that the walk classifies correctly and is then
    /// DENIED permission to unlink.
    /// <para>
    /// This pins the argument for leaving the swallow at this call site alone. That argument is entirely about
    /// which branch the refusal lands on: the comment above the catch says the best-effort warning "is exactly
    /// the sentence that would hide a planted link forever", and the whole defence of not rethrowing is that
    /// the operator still gets an Error naming an address to go and look at. A denied unlink used to arrive as
    /// a raw <see cref="IOException"/> and land in precisely that hiding warning; it now arrives typed and
    /// lands on the Error branch. Nothing asserted that, so widening the generic catch below, or swapping the
    /// two, would drop it back to the hiding sentence with a fully green suite — and the comment would go on
    /// reading as though it were still true.
    /// </para>
    /// <para>
    /// Both legs are asserted because neither alone identifies a branch: both handlers log, and both render
    /// the same <c>{HostDir}</c>. What separates them is which one fired, so "Error fired" without "Warning
    /// did not" would still pass if the refusal had gone to the warning and something else logged an error.
    /// </para>
    /// <para>
    /// The third leg is the exception argument, and it is asserted through the exception rather than the
    /// rendered text because that is the only place it survives. The Error template carries <c>{HostDir}</c> —
    /// the slot directory, which is not the offending entry and is the same value the warning renders — so the
    /// address an operator is actually sent to reaches them ONLY through the exception passed to
    /// <see cref="LoggerExtensions.LogError(ILogger, Exception, string, object?[])"/>. Drop that argument and
    /// the two assertions above still pass while the line degrades into a report that something under this slot
    /// was refused, without saying what: the hiding failure this site exists to prevent, arriving on the branch
    /// the test just proved was the good one.
    /// </para>
    /// </summary>
    [RequiresUnreadableEntryFact("a removable link cannot show what happens when the unlink is refused")]
    public async Task DestroyAsync_logs_a_refused_unlink_as_an_error_rather_than_a_best_effort_warning()
    {
        var hostRoot = Path.Combine(_tempRoot, "workspaces");
        var hostDir = Path.Combine(hostRoot, "review-run-7");
        var checkout = Path.Combine(hostDir, "checkout");
        _ = Directory.CreateDirectory(checkout);
        var outside = Path.Combine(_tempRoot, "outside");
        _ = Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "someone-elses.txt");
        await File.WriteAllTextAsync(victim, "notes");
        var planted = Path.Combine(checkout, "planted");
        using var undeletable = UnreadableEntry.UndeletableLink(planted, outside);

        var fake = new FakeSessionSource();
        var loggerFactory = new CapturingLoggerFactory();
        var provisioner = new ReviewSessionProvisioner(
            fake,
            new CodeReviewDaemonOptions { WorkspaceHostRoot = hostRoot },
            loggerFactory,
            workspaceBasePath: "/ws",
            diskSpaceProbe: SufficientDisk);

        await provisioner.DestroyAsync(Run(), default);

        loggerFactory.Capturing.CountAtLevel(LogLevel.Error, "REFUSED").Should().Be(
            1,
            "an operator who is never told the teardown hit an entry it may not remove has no reason to go "
                + "looking for the one that is still sitting there");
        loggerFactory.Capturing.CountAtLevel(LogLevel.Warning, "Best-effort host-dir cleanup failed")
            .Should().Be(
                0,
                "this is the sentence the catch comment calls out by name — reported as a transient nuisance, "
                    + "a planted link stays hidden for as long as anyone cares to skim");
        loggerFactory.Capturing.CountAtLevelWithExceptionText(LogLevel.Error, planted).Should().Be(
            1,
            "the Error template renders the slot directory, not the entry, so the only address the operator "
                + "can act on rides in on the exception — an error that says something here was refused, "
                + "without saying what, sends them to a tree to search by hand");
        Directory.Exists(planted).Should().BeTrue(
            "the entry that stopped the wipe is left exactly as found — it was refused, not raced");
        (await File.ReadAllTextAsync(victim)).Should().Be("notes", "nothing may reach through the link");
        fake.DestroyedWorkspaceIds.Should().Contain(
            "review-run-7", "the refusal is logged, not thrown, so the rest of the teardown still ran");
    }

    /// <summary>
    /// A redirected entry is removed by NAME — never by a recursive delete — and a file symlink is the only
    /// input that can show the difference. For a junction the two spellings are indistinguishable:
    /// <see cref="Directory.Delete(string, bool)"/> applied to the reparse point ITSELF does not recurse into
    /// it, so the target survives either way. (It throws only when the recursion of an ANCESTOR walks onto a
    /// junction, which is a different call and why the wipe unlinks as it goes.) A file symlink is not a
    /// directory at all, so a recursive directory delete fails on it, the wipe never completes, and a
    /// cleanable link becomes a permanent teardown failure.
    /// </summary>
    [RequiresFileSymlinkFact("a junction always reads as a directory, so it cannot distinguish the file branch")]
    public async Task DestroyAsync_unlinks_a_file_symlink_inside_the_workspace_and_still_completes()
    {
        var hostRoot = Path.Combine(_tempRoot, "workspaces");
        var hostDir = Path.Combine(hostRoot, "review-run-7");
        var outside = Path.Combine(_tempRoot, "outside");
        _ = Directory.CreateDirectory(hostDir);
        _ = Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "someone-elses.txt");
        await File.WriteAllTextAsync(victim, "notes");
        FileLink.Create(Path.Combine(hostDir, "notes.txt"), victim);

        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            fake,
            new CodeReviewDaemonOptions { WorkspaceHostRoot = hostRoot },
            NullLoggerFactory.Instance,
            workspaceBasePath: "/ws",
            diskSpaceProbe: SufficientDisk);

        await provisioner.DestroyAsync(Run(), default);

        (await File.ReadAllTextAsync(victim)).Should().Be("notes", "nothing may reach through the link");
        Directory.Exists(hostDir).Should().BeFalse(
            "removing the link by name is what lets the teardown finish; a recursive directory delete would "
                + "fail on a file symlink and leave the workspace behind");
    }

    /// <summary>
    /// In-memory <see cref="ISandboxSessionSource"/> that mimics <see cref="SandboxSessionRegistry"/>'s own
    /// per-workspace-id session caching, so the provisioner's behavior is verifiable without a live
    /// gateway: a second request for the same workspace id returns the same <see cref="SandboxSession"/>
    /// (same <c>SessionId</c>) and does not bump <see cref="CreateCount"/>.
    /// </summary>
    private sealed class FakeSessionSource : ISandboxSessionSource
    {
        private readonly Dictionary<string, SandboxSession> _sessions = new(StringComparer.Ordinal);

        public int CreateCount { get; private set; }

        public List<string> DestroyedWorkspaceIds { get; } = [];

        /// <summary>The most recent <see cref="WorkspaceRef"/> the provisioner asked to mount — lets a test
        /// assert the session key (<c>Id</c>) and the mounted directory (<c>DirectoryRelPath</c>).</summary>
        public WorkspaceRef? LastRef { get; private set; }

        public Task<SandboxSession> GetOrCreateLiveSessionAsync(WorkspaceRef workspaceRef, CancellationToken ct)
        {
            LastRef = workspaceRef;
            if (!_sessions.TryGetValue(workspaceRef.Id, out var session))
            {
                CreateCount++;
                session = new SandboxSession(
                    workspaceRef.Id,
                    $"session-{workspaceRef.Id}",
                    workspaceRef.Id,
                    $"/workspace/{workspaceRef.Id}");
                _sessions[workspaceRef.Id] = session;
            }

            return Task.FromResult(session);
        }

        public Task DestroyWorkspaceSessionAsync(string workspaceId, CancellationToken ct)
        {
            DestroyedWorkspaceIds.Add(workspaceId);
            _ = _sessions.Remove(workspaceId);
            return Task.CompletedTask;
        }
    }
}
