using System.Text.Json;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// What the reviewer is told about the build/test tooling in its container (#272). Revobot reported that it
/// could not verify a finding because <c>dotnet: not found</c> — it discovered the gap by trying, once per
/// run, then fell back to reasoning about the code alone. That fallback is exactly where a plausible-but-wrong
/// finding survives, and nothing in the review said the verification had not happened.
/// <para>
/// The code-side answer is to establish the fact once and state it: the reviewer is told whether it can build,
/// and when it cannot it is told to mark unexecuted findings as such rather than burn turns on a command that
/// cannot succeed. Provisioning an SDK into the review image is deliberately NOT part of this — see the PR
/// description.
/// </para>
/// </summary>
public sealed class SandboxBuildToolingTests
{
    [Fact]
    public async Task NoSdkInTheContainer_TheReviewerIsToldSo_InsteadOfDiscoveringItByRunningDotnet()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContainsFirst("dotnet --version", new SandboxCommandResult(127, "", "dotnet: not found"));
        var factory = new FakeReviewAgentLoopFactory();
        var executor = BuildExecutor(store, factory, runner);

        await executor.ExecuteStageAsync(
            ReviewStage.Reviewed, SeedRunWithContext(store, "270"), CancellationToken.None);

        var prompt = factory.CreatedProfiles[0].SystemPrompt;
        prompt.Should().Contain("no .NET SDK is installed");
        // Not just the fact — the consequence. A reviewer told "no SDK" but not told what to do with a finding
        // it could not execute still writes that finding as though it had checked.
        prompt.Should().Contain("unverified");
    }

    [Fact]
    public async Task AnSdkInTheContainer_TheReviewerIsToldItCanVerifyByRunningAFocusedTest()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContainsFirst("dotnet --version", new SandboxCommandResult(0, "9.0.100\n", ""));
        var factory = new FakeReviewAgentLoopFactory();
        var executor = BuildExecutor(store, factory, runner);

        await executor.ExecuteStageAsync(
            ReviewStage.Reviewed, SeedRunWithContext(store, "270"), CancellationToken.None);

        var prompt = factory.CreatedProfiles[0].SystemPrompt;
        prompt.Should().Contain("9.0.100", "the reviewer is told WHICH SDK it has, not merely that it has one");
        prompt.Should().Contain("dotnet test");
        prompt.Should().NotContain("no .NET SDK is installed");
    }

    [Fact]
    public async Task AFirstRunBanner_DoesNotLandInThePrompt_OnlyTheVersionBehindIt()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        // What `dotnet --version` actually prints the FIRST time it runs in a fresh container: the version is
        // the last line, after the whole first-use banner.
        const string firstRun = """
            Welcome to .NET 9.0!
            ---------------------
            SDK Version: 9.0.100

            Telemetry
            ---------
            The .NET tools collect usage data in order to help us improve your experience.

            --------------------------------------------------------------------------------
            9.0.100
            """;
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContainsFirst("dotnet --version", new SandboxCommandResult(0, firstRun, ""));
        var factory = new FakeReviewAgentLoopFactory();
        var executor = BuildExecutor(store, factory, runner);

        await executor.ExecuteStageAsync(
            ReviewStage.Reviewed, SeedRunWithContext(store, "270"), CancellationToken.None);

        // The status sentence is interpolated verbatim into the reviewer's system prompt, so an unfiltered
        // stdout spends its context on a telemetry notice and buries the one fact the line exists to carry.
        var prompt = factory.CreatedProfiles[0].SystemPrompt;
        prompt.Should().Contain("dotnet 9.0.100");
        prompt.Should().NotContain("Telemetry");
        prompt.Should().NotContain("Welcome to .NET");
    }

    [Fact]
    public async Task AFailedProbeThatStillPrinted_IsAbsence_NotAVersionMadeOfItsErrorText()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        // A container carrying the `dotnet` muxer but no SDK: the command runs, prints to stdout, and fails.
        var runner = new FakeSandboxCommandRunner().OnArgvContainsFirst(
            "dotnet --version",
            new SandboxCommandResult(145, "You must install or update .NET to run this application.\n", ""));
        var factory = new FakeReviewAgentLoopFactory();
        var executor = BuildExecutor(store, factory, runner);

        await executor.ExecuteStageAsync(
            ReviewStage.Reviewed, SeedRunWithContext(store, "270"), CancellationToken.None);

        // Reading stdout without checking the exit code would announce that sentence to the reviewer as its
        // SDK version, and send it off to run builds that cannot work.
        var prompt = factory.CreatedProfiles[0].SystemPrompt;
        prompt.Should().Contain("no .NET SDK is installed");
        prompt.Should().NotContain("You must install or update .NET");
    }

    [Fact]
    public async Task AProbeThatSucceedsButPrintsNothing_IsNotReportedAsAnSdkWithoutAVersion()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContainsFirst("dotnet --version", new SandboxCommandResult(0, "  \n", ""));
        var factory = new FakeReviewAgentLoopFactory();
        var executor = BuildExecutor(store, factory, runner);

        await executor.ExecuteStageAsync(
            ReviewStage.Reviewed, SeedRunWithContext(store, "270"), CancellationToken.None);

        // Trusting the exit code alone yields "a .NET SDK is available (dotnet )" — a claim with a hole where
        // its evidence should be. An exit code with no version behind it is not a detection.
        factory.CreatedProfiles[0].SystemPrompt.Should().Contain("no .NET SDK is installed");
    }

    [Fact]
    public async Task TheProbeIsIndeterminateWhenItCannotRun_AndSaysSo_RatherThanClaimingTheSdkIsAbsent()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var executor = BuildExecutor(store, factory, new UnprobeableCommandRunner());
        var run = SeedRunWithContext(store, "270");

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // "The probe could not run" is not evidence the SDK is missing. Reporting it as missing would tell a
        // reviewer sitting on a container that CAN build to stop trying.
        var prompt = factory.CreatedProfiles[0].SystemPrompt;
        prompt.Should().Contain("could not determine");
        prompt.Should().NotContain("no .NET SDK is installed");
        // And the INSTRUCTION has to be indeterminate too, not merely the sentence above it. Stating "unknown"
        // and then issuing the absent branch's order is a contradiction the reviewer resolves by obeying the
        // order — which loses the verification on every container that could in fact have built.
        prompt.Should().NotContain("do NOT spend turns");
        prompt.Should().Contain("try the build once");
        // And a probe that cannot run must never cost the review.
        store.GetArtifacts(run.Id)
            .Should().Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    [Fact]
    public async Task AnIndeterminateProbe_IsRetriedOnTheNextReview_NotCachedForTheProcessLifetime()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runner = new UnprobeableCommandRunner();
        var executor = BuildExecutor(store, new FakeReviewAgentLoopFactory(), runner);

        await executor.ExecuteStageAsync(
            ReviewStage.Reviewed, SeedRunWithContext(store, "270"), CancellationToken.None);
        await executor.ExecuteStageAsync(
            ReviewStage.Reviewed, SeedRunWithContext(store, "271"), CancellationToken.None);

        // A detected verdict is process-lifetime configuration and is cached. A FAILED READ is not a verdict:
        // caching it lets one transient gateway hiccup disable build-verification for every review this
        // process runs afterwards. Same rule the gateway skill probe already follows.
        runner.ProbeAttempts.Should().Be(2, "a read failure must not be cached as a verdict");
    }

    [Fact]
    public async Task TheContainerIsProbedOncePerProcess_NotOncePerReview()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContainsFirst("dotnet --version", new SandboxCommandResult(0, "9.0.100\n", ""));
        var executor = BuildExecutor(store, new FakeReviewAgentLoopFactory(), runner);

        await executor.ExecuteStageAsync(
            ReviewStage.Reviewed, SeedRunWithContext(store, "270"), CancellationToken.None);
        await executor.ExecuteStageAsync(
            ReviewStage.Reviewed, SeedRunWithContext(store, "271"), CancellationToken.None);

        // The image is process-lifetime gateway configuration, like the marketplace catalog. Re-probing per
        // review adds a blocking round-trip to every run for an answer that cannot have changed.
        runner.Commands
            .Count(c => string.Join(' ', c.Argv).Contains("dotnet --version", StringComparison.Ordinal))
            .Should().Be(1);
    }

    [Fact]
    public async Task AnUncancelledTimeoutProbing_IsIndeterminate_NotAFaultedReview()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var runner = new TimingOutCommandRunner();
        var factory = new FakeReviewAgentLoopFactory();
        var executor = BuildExecutor(store, factory, runner);
        var run = SeedRunWithContext(store, "270");

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The production ISandboxCommandRunner converts its OWN timeout into TimeoutException, so this site is
        // correct only by accident of one implementation. The port permits any runner, and a runner that lets a
        // timeout-shaped TaskCanceledException out faults a review the probe promises never to cost.
        runner.ProbeAttempts.Should().Be(1, "the site under test is only reached if the probe actually ran");
        factory.CreatedProfiles[0].SystemPrompt.Should().Contain("could not determine");
        store.GetArtifacts(run.Id)
            .Should().Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    [Fact]
    public async Task CallerRequestedCancellationDuringTheProbe_Propagates_RatherThanReadingAsAnUnknownSdk()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        using var cts = new CancellationTokenSource();
        var runner = new CancellingCommandRunner(cts);
        var executor = BuildExecutor(store, new FakeReviewAgentLoopFactory(), runner);
        var run = SeedRunWithContext(store, "270");

        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, cts.Token);

        // Shutdown must not be swallowed as "SDK unknown" and allowed to run a whole review during a cancel.
        _ = await act.Should().ThrowAsync<OperationCanceledException>();
        runner.ProbeAttempts.Should().Be(1, "a pre-cancelled token would pass this test without reaching the site");
        store.GetArtifacts(run.Id)
            .Should().NotContain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    private static DaemonReviewStageExecutor BuildExecutor(
        ReviewStore store,
        FakeReviewAgentLoopFactory factory,
        ISandboxCommandRunner runner) =>
        new(
            store,
            factory,
            runner,
            new FakeSandboxFileSystem(),
            new CodeReviewDaemonOptions(),
            [new FakeReviewCommentPublisher("github")],
            NullLoggerFactory.Instance);

    /// <summary>
    /// Seeds a run plus the 'review-context' artifact the Reviewed stage reads, so a test can drive that stage
    /// directly without first running ContextReady.
    /// </summary>
    private static ReviewRun SeedRunWithContext(ReviewStore store, string prId)
    {
        var repoId = store.EnsureRepo(new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "repo-stable-1",
        });
        var run = store.CreateOrGetReviewRun(new ReviewRun
        {
            RepoId = repoId,
            PrId = prId,
            HeadSha = $"head-{prId}",
            BaseSha = $"base-{prId}",
            TriggerWatermark = $"wm-{prId}",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        });

        _ = store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = DaemonReviewStageExecutor.ContextArtifactSchemaVersion,
            ArtifactKind = DaemonReviewStageExecutor.ContextArtifactKind,
            Provider = "github",
            Payload = JsonSerializer.Serialize(new ContextArtifactPayload(
                run.PrId, run.BaseSha, run.HeadSha, "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;")),
        });

        return run;
    }

    /// <summary>
    /// A gateway that refuses the probe specifically — a different fact from a container without an SDK, and
    /// the one that must not be reported as absence. Everything else the stage runs behaves normally, so the
    /// test is about the probe's verdict rather than about a broken sandbox.
    /// </summary>
    private sealed class UnprobeableCommandRunner : ISandboxCommandRunner
    {
        private readonly FakeSandboxCommandRunner _inner = new();

        /// <summary>How many times the SDK probe was attempted — the retry-vs-cache discriminator.</summary>
        public int ProbeAttempts { get; private set; }

        public Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken cancellationToken)
        {
            if (!string.Join(' ', command.Argv).Contains("dotnet --version", StringComparison.Ordinal))
            {
                return _inner.RunAsync(command, cancellationToken);
            }

            ProbeAttempts++;
            throw new InvalidOperationException("gateway session unavailable");
        }
    }

    /// <summary>
    /// A runner whose probe merely times out, surfacing it the way <c>HttpClient</c> and
    /// <c>Task.WaitAsync</c> both do — a <see cref="TaskCanceledException"/> raised with the caller's token
    /// still uncancelled. The production adapter happens to convert its own timeout into
    /// <c>TimeoutException</c> instead, but that is a property of one implementation, not of the port.
    /// </summary>
    private sealed class TimingOutCommandRunner : ISandboxCommandRunner
    {
        private readonly FakeSandboxCommandRunner _inner = new();

        public int ProbeAttempts { get; private set; }

        public Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken cancellationToken)
        {
            if (!string.Join(' ', command.Argv).Contains("dotnet --version", StringComparison.Ordinal))
            {
                return _inner.RunAsync(command, cancellationToken);
            }

            ProbeAttempts++;
            throw new TaskCanceledException("the probe exceeded its own timeout", new TimeoutException());
        }
    }

    /// <summary>
    /// A probe interrupted by the CALLER — daemon shutdown. The cancel is raised from inside the run so the
    /// catch under test is actually reached; a pre-cancelled token is refused earlier in the stage.
    /// </summary>
    private sealed class CancellingCommandRunner(CancellationTokenSource cts) : ISandboxCommandRunner
    {
        private readonly FakeSandboxCommandRunner _inner = new();

        public int ProbeAttempts { get; private set; }

        public Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken cancellationToken)
        {
            if (!string.Join(' ', command.Argv).Contains("dotnet --version", StringComparison.Ordinal))
            {
                return _inner.RunAsync(command, cancellationToken);
            }

            ProbeAttempts++;
            cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return _inner.RunAsync(command, cancellationToken);
        }
    }
}
