using System.Text.Json;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Drives <see cref="DaemonReviewStageExecutor"/> through the ContextReady stage on the host-prepared pooled
/// path — the path every live profile takes — so a test can assert on the review context artifact the daemon
/// actually hands the reviewer.
/// </summary>
/// <remarks>
/// Shared rather than duplicated because the setup is the expensive part and the scenarios differ only in
/// what they script: <see cref="UntrustedMountDenialTests"/> varies the mount and the run's trust,
/// <see cref="LostDiffGuardTests"/> varies what git returns for the diff. <see cref="Runner"/> is exposed for
/// exactly that — <c>OnArgvContainsFirst</c> lets a test refine the broad rules set here without rebuilding
/// them.
/// </remarks>
internal sealed class ContextStageHarness : IDisposable
{
    internal const string StoreUrl = "https://github.com/achieveai/AchieveAiReviews.git";

    /// <summary>The mount a DEPOT-shaped pool hands out: one directory serving every repository, which is
    /// what makes a sibling's checkout reachable from a run that was never granted it.</summary>
    internal const string DepotMount = "/pool/review-depot";

    /// <summary>Today's shape: a mount created for one repository key and holding only its store.</summary>
    internal const string DedicatedMount = "/pool/review-lmdotnettools-1a2b3c4d";

    internal const string Gitmodules = """
        [submodule "LmDotnetTools"]
        	path = repos/LmDotnetTools
        	url = https://github.com/achieveai/LmDotnetTools.git
        [submodule "Nova"]
        	path = repos/Nova
        	url = https://github.com/achieveai/Nova.git
        [submodule "Contracts"]
        	path = Contracts
        	url = https://github.com/achieveai/Contracts.git
        """;

    internal static readonly RepoIdentity Repo = new()
    {
        Provider = "github",
        OrgOrOwner = "achieveai",
        RepoName = "LmDotnetTools",
        RepoStableId = "repo-stable-1",
    };

    private readonly TempSqliteDatabase _db;
    private readonly HttpClient? _s2sHttp;

    /// <param name="mount">The mount the pool hands out — <see cref="DepotMount"/> for a cross-repo depot,
    /// anything else for a per-repo one.</param>
    /// <param name="s2s">Routes context through the host-prepared pooled path, as every live profile does.</param>
    /// <param name="wireS2SPreparer">
    /// Wires the real <see cref="S2SReviewWorkspacePreparer"/>. That object's mere PRESENCE is what tells the
    /// executor a per-PR degrade would mint an unreclaimed host clone + LmStreaming workspace, so it is what
    /// turns a refusal from a fallback into a throw.
    /// </param>
    public ContextStageHarness(string mount, bool s2s, bool wireS2SPreparer = false)
    {
        _db = new TempSqliteDatabase();
        Store = new ReviewStore(_db.ConnectionString);
        Pool = new ScriptedSlotPool(mount);
        HostFileSystem = new FakeSandboxFileSystem();
        _ = HostFileSystem.Seed($"{mount}/store/.gitmodules", Gitmodules);

        // The in-process degrade clones into /workspace/target through the boot runner, so its
        // is-inside-work-tree probe must fail the way an empty directory does.
        Runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                "rev-parse --is-inside-work-tree", new SandboxCommandResult(1, string.Empty, "not a git repo"))
            .OnArgvContains(
                "diff", new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ x", string.Empty));

        var options = new CodeReviewDaemonOptions
        {
            EnableToolAssistedReview = true,
            EnableReviewerWrites = true,
            CrossRepoStoreUrl = StoreUrl,
            UseS2SReviewAgent = s2s,
            LmStreamingBaseUrl = s2s ? "http://localhost:5051" : null,
            CrossRepoSiblings = ["achieveai/Nova"],
        };

        S2SReviewWorkspacePreparer? preparer = null;
        if (wireS2SPreparer)
        {
            _s2sHttp = new HttpClient(new FakeHttpMessageHandler())
            {
                BaseAddress = new Uri("http://localhost:5051/"),
            };
            preparer = new S2SReviewWorkspacePreparer(
                new LmStreamingS2SClient(_s2sHttp, "secret", "app-id", "app-key"),
                new GitRunner(Runner),
                "/pool",
                reviewMarketplace: "code-reviewer",
                NullLogger<S2SReviewWorkspacePreparer>.Instance);
        }

        Executor = new DaemonReviewStageExecutor(
            Store,
            new FakeReviewAgentLoopFactory(),
            Runner,
            new FakeSandboxFileSystem(),
            options,
            [new FakeReviewCommentPublisher("github")],
            NullLoggerFactory.Instance,
            slotWorkspace: new ReviewSlotWorkspace(
                Pool,
                new ScriptedSlotPreparer(),
                (_, _) => new ScriptedSlotPreparer(),
                Runner,
                HostFileSystem),
            preparer: preparer);
    }

    public ReviewStore Store { get; }

    public ScriptedSlotPool Pool { get; }

    public FakeSandboxFileSystem HostFileSystem { get; }

    public DaemonReviewStageExecutor Executor { get; }

    /// <summary>The scripted git, exposed so a test can refine what a specific command returns.</summary>
    public FakeSandboxCommandRunner Runner { get; }

    /// <summary>
    /// Puts the sibling repositories on disk under the mount's store — what a depot looks like after some
    /// OTHER repo's review checked them out.
    /// </summary>
    public void SeedPopulatedSiblings()
    {
        _ = HostFileSystem.Seed($"{Pool.Mount}/store/repos/Nova/README.md", "# Nova");
        _ = HostFileSystem.Seed($"{Pool.Mount}/store/Contracts/Contracts.csproj", "<Project />");
    }

    public ReviewRun SeedRun(bool isForkPr, bool isTargetRepoPublic) =>
        Store.CreateOrGetReviewRun(new ReviewRun
        {
            RepoId = Store.EnsureRepo(Repo),
            PrId = "118",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
            IsForkPr = isForkPr,
            IsTargetRepoPublic = isTargetRepoPublic,
        });

    public void Dispose()
    {
        _s2sHttp?.Dispose();
        Store.Dispose();
        _db.Dispose();
    }

    /// <summary>The single review context artifact the ContextReady stage persisted.</summary>
    public JsonElement ContextPayload(ReviewRun run) =>
        JsonDocument.Parse(Store.GetArtifacts(run.Id).Should().ContainSingle().Subject.Payload).RootElement;

    /// <summary>The sibling PATHS the brief would hand the reviewer.</summary>
    public IReadOnlyList<string> SiblingPaths(ReviewRun run)
    {
        var payload = ContextPayload(run);
        if (!payload.TryGetProperty("SiblingRepos", out var siblings)
            || siblings.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        return [.. siblings.EnumerateArray().Select(s => s.GetProperty("Path").GetString() ?? string.Empty)];
    }
}

/// <summary>
/// A pool that hands every run the SAME mount, so a test decides by its <c>mount</c> argument whether that
/// mount is a per-repo one or a cross-repo depot. It reports the leased repo key faithfully, which is what
/// makes the depot case realistic: the naive shared-depot change keeps leasing on the repo key and only
/// relocates the store, so a check that trusted the key alone would still pass.
/// </summary>
internal sealed class ScriptedSlotPool(string mount) : IReviewSlotPool
{
    private int _next;

    public string Mount { get; } = mount;

    public List<ReviewSlot> Returned { get; } = [];

    /// <summary>A depot mount serves every repository; a per-repo mount serves one. This is the single
    /// declaration that separates the two layouts in these tests.</summary>
    public bool MountIsDedicatedTo(string repoKey) =>
        !string.Equals(Mount, ContextStageHarness.DepotMount, StringComparison.Ordinal);

    public Task<ReviewSlot> LeaseAsync(string repoKey, CancellationToken cancellationToken)
    {
        var index = _next++;
        var slotDir = $"slot-{index}";
        return Task.FromResult(new ReviewSlot(
            Index: index,
            HostPath: Mount,
            StorePath: $"{Mount}/{slotDir}/notes",
            ScratchPath: $"{Mount}/{slotDir}/scratch",
            RepoKey: repoKey,
            SharedStorePath: $"{Mount}/store",
            TargetPath: $"{Mount}/{slotDir}/repo",
            SlotDirName: slotDir));
    }

    public Task<ReviewSlot> LeaseAsync(CancellationToken cancellationToken) =>
        LeaseAsync(string.Empty, cancellationToken);

    public Task ReturnAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        Returned.Add(slot);
        return Task.CompletedTask;
    }
}

/// <summary>Returns the checkout the real preparer would produce, rooted at the slot's own paths.</summary>
internal sealed class ScriptedSlotPreparer : IReviewSlotPreparer
{
    public Task<PreparedCheckout> PrepareAsync(
        ReviewSlot slot,
        ReviewRun run,
        string storeUrl,
        string submoduleRelPath,
        string branch,
        string defaultBranch,
        string notesRelPath,
        OperationPolicy policy,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PreparedCheckout(
            slot.UsesSharedStore ? slot.StorePath : $"{slot.StorePath}",
            slot.UsesSharedStore ? slot.TargetPath : $"{slot.StorePath}/{submoduleRelPath}",
            $"{slot.StorePath}/{notesRelPath}",
            branch));
}
