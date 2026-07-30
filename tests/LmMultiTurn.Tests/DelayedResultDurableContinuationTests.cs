using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using LmMultiTurn.Tests.Lifecycle;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Covers the continuation a delayed result is owed surviving the things that can happen to the
/// process carrying it: the requesting run parking mid-resolution, a store that will not name the
/// child run, a restart between committing a result and running it, and a resolver whose token is
/// cancelled the instant its delivery returns.
/// </summary>
/// <remarks>
/// <para>
/// A resolution that arrives while its requesting run is still going is folded into that run. One
/// that arrives after it parked needs a child run to carry it. The two are decided by a flag another
/// thread sets, so a resolution can begin as the first and finish as the second — and the durable
/// record has to end up naming the child run either way, or a process that dies before the child
/// runs leaves a result in history that nothing will ever carry to the model.
/// </para>
/// <para>
/// Separate from <c>DelayedResultChildRunTests</c>, which covers the run graph a resolution produces
/// when nothing goes wrong.
/// </para>
/// </remarks>
public class DelayedResultDurableContinuationTests
{
    /// <summary>How long a deterministic signal is given before the test is called stuck.</summary>
    /// <remarks>
    /// Nothing here waits on a duration — every wait is on a gate another thread opens. This is only
    /// the bound that turns a deadlock into a failure instead of a hung run.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    #region Parking during the resolution write

    [Fact]
    public async Task AResolutionThatRacesParking_DurablyNamesTheChildRunItCauses()
    {
        await using var race = await ParkRace.StartAsync();

        var outcome = await race.ReleaseAndFinishAsync();

        outcome.Should().Be(ResolveToolCallOutcome.Resolved);
        var record = await race.RecordAsync("tc_race");
        record.IsResolved.Should().BeTrue();
        record
            .ChildRunId.Should()
            .NotBeNull(
                "the run parked while this resolution was being written, so the result can only "
                    + "reach the model as a child run — and a child run nothing recorded is one no "
                    + "restart can find"
            );

        // The run that actually carried it is the run the record names: recovery keys off that id, so
        // a record naming a different run than the one that ran would either resurrect a finished
        // continuation or miss a live one.
        await race.Watcher.WaitForCompletionsAsync(2);
        var child = race
            .Publisher.Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Should()
            .ContainSingle(r => r.Cause.ToolCallId == "tc_race")
            .Subject;
        child.RunId.Should().Be(record.ChildRunId);
    }

    [Fact]
    public async Task WhenTheChildRunCannotBeNamed_TheResolutionIsRefusedAndTheRetryLands()
    {
        await using var race = await ParkRace.StartAsync();
        race.Store.FailAttach = true;

        var refused = await race.ReleaseAndFinishAsync();

        refused
            .Should()
            .Be(
                ResolveToolCallOutcome.StoreFailed,
                "naming the continuation is not optional — a resolution that cannot record the run "
                    + "that will carry it must not be reported as having happened"
            );
        var pending = await race.Loop.GetDeferredToolCallsAsync();
        pending
            .Should()
            .Contain(
                p => p.ToolCallId == "tc_race",
                "the claim went back, so the caller can deliver the same result again"
            );
        var results = await race.ResultsAsync();
        results
            .Single(m => m.ToolCallId == "tc_race")
            .IsDeferred.Should()
            .BeTrue("history was never touched, which is what makes the retry clean");

        // What the refusal leaves behind, stated exactly: the resolution record stands (it committed
        // before the attach was attempted), naming no child. That is the state the retry has to cope
        // with, and the next assertion is that it does.
        var afterRefusal = await race.RecordAsync("tc_race");
        afterRefusal.IsResolved.Should().BeTrue();
        afterRefusal.ChildRunId.Should().BeNull();

        race.Store.FailAttach = false;
        var retried = await race.Loop.TryResolveToolCallAsync("tc_race", ParkRace.Answer);

        retried
            .Should()
            .Be(
                ResolveToolCallOutcome.Resolved,
                "the retry is the delivery that resolves the call, even though the durable record "
                    + "was already carrying its result"
            );
        var record = await race.RecordAsync("tc_race");
        record
            .ChildRunId.Should()
            .NotBeNull("the retry has to finish the job the refused attempt could not");

        await race.Watcher.WaitForCompletionsAsync(2);
        race.Publisher.Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Should()
            .ContainSingle(r => r.Cause.ToolCallId == "tc_race")
            .Which.RunId.Should()
            .Be(record.ChildRunId);
    }

    [Fact]
    public async Task WhenAnotherProcessAlreadyNamedTheChild_ThisOneAdoptsItRatherThanStartingASecond()
    {
        await using var race = await ParkRace.StartAsync();

        // Stands in for the process that resolved this call, named its continuation, and died before
        // running it. Its name is in the record before this process gets to write its own.
        race.Store.BeforeAttach = (threadId, toolCallId) =>
            race.Store.Inner.AttachDeferredChildRunAsync(
                threadId,
                toolCallId,
                "child-from-the-dead-process",
                DateTimeOffset.UtcNow
            );

        var outcome = await race.ReleaseAndFinishAsync();

        outcome.Should().Be(ResolveToolCallOutcome.Resolved);
        (await race.RecordAsync("tc_race")).ChildRunId.Should().Be("child-from-the-dead-process");

        await race.Watcher.WaitForCompletionsAsync(2);
        race.Publisher.Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Should()
            .ContainSingle(r => r.Cause.ToolCallId == "tc_race")
            .Which.RunId.Should()
            .Be(
                "child-from-the-dead-process",
                "two runs for one result would send the conversation to the provider twice; the "
                    + "committed name is the one that stands"
            );
    }

    #endregion

    #region Surviving a restart

    [Fact]
    public async Task AContinuationCommittedButNeverRun_ResumesExactlyOnceAfterARestart()
    {
        var store = new InMemoryConversationStore();
        var threadId = $"thread-{Guid.NewGuid():N}";
        var committedChildRunId = await CommitAContinuationThatNeverRanAsync(store, threadId);

        // The process that replaces the one that died mid-continuation.
        var provider = new Provider();
        var publisher = new RecordingLifecyclePublisher();
        await using var resumed = NewLoop(provider, threadId, store, publisher, out var cts);
        (await resumed.RecoverAsync()).Should().BeTrue();
        await using var watcher = new MessageWatcher(resumed);
        _ = resumed.RunAsync(cts.Token);

        await watcher.WaitForCompletionsAsync(1);

        var children = publisher
            .Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Where(r => r.Cause.Kind == LifecycleRunCauseKinds.ToolResult)
            .ToList();
        children
            .Should()
            .ContainSingle("the result was owed exactly one continuation, and it is owed exactly one now")
            .Which.RunId.Should()
            .Be(
                committedChildRunId,
                "reusing the committed id is what makes a second crash idempotent — the run row is "
                    + "the record of having begun"
            );
        children[0].Cause.ToolCallId.Should().Be("tc_1");

        provider
            .CallCount.Should()
            .Be(1, "the recovered continuation is the only reason this process talks to a model at all");
        ResultsFor(provider.RequestAt(0), "tc_1")
            .Should()
            .ContainSingle("the result was already in history; carrying it as a cause must not append it again")
            .Which.Should()
            .Be("the-answer");
        (await LoadResultsAsync(store, threadId))
            .Should()
            .ContainSingle(m => m.ToolCallId == "tc_1")
            .Which.IsDeferred.Should()
            .BeFalse();
        await cts.CancelAsync();
    }

    [Fact]
    public async Task ARecoveredContinuationIsNotRecoveredASecondTime()
    {
        var store = new InMemoryConversationStore();
        var threadId = $"thread-{Guid.NewGuid():N}";
        _ = await CommitAContinuationThatNeverRanAsync(store, threadId);

        await using (var resumed = NewLoop(new Provider(), threadId, store, null, out var firstCts))
        {
            (await resumed.RecoverAsync()).Should().BeTrue();
            await using var firstWatcher = new MessageWatcher(resumed);
            _ = resumed.RunAsync(firstCts.Token);
            await firstWatcher.WaitForCompletionsAsync(1);
            await firstCts.CancelAsync();
        }

        var provider = new Provider();
        var publisher = new RecordingLifecyclePublisher();
        await using var later = NewLoop(provider, threadId, store, publisher, out var cts);
        (await later.RecoverAsync()).Should().BeTrue();
        await using var watcher = new MessageWatcher(later);
        _ = later.RunAsync(cts.Token);

        // A run started by ordinary input is the deterministic proof that nothing was owed: delayed
        // causes outrank input and inputs are held while any cause is pending, so this run could not
        // have started until the recovered queue was empty.
        _ = await later.SendAsync([new TextMessage { Text = "anything new?", Role = Role.User }]);
        await watcher.WaitForCompletionsAsync(1);

        publisher
            .Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Should()
            .NotContain(
                r => r.Cause.Kind == LifecycleRunCauseKinds.ToolResult,
                "the continuation ran in the previous process, and its run row says so"
            );
        provider
            .CallCount.Should()
            .Be(1, "only the follow-up turn talks to the model; the result is not carried a second time");
        await cts.CancelAsync();
    }

    [Fact]
    public async Task AChildRunThatCannotBeRecordedAsStarted_IsNotRunAndIsRecoveredExactlyOnce()
    {
        var store = new InMemoryConversationStore();
        var threadId = $"thread-{Guid.NewGuid():N}";
        var committedChildRunId = await CommitAContinuationThatNeverRanAsync(store, threadId);

        // The lifecycle write that would mark this continuation as begun fails — a disk error, a
        // closed connection. Recording a start is best-effort everywhere else in the loop, and this
        // is the one place where carrying on regardless would be a correctness bug rather than a
        // reporting gap: the missing row is exactly what the next process reads as "never ran".
        var crippled = new SteerableLifecycleStore(store)
        {
            BeforeRunStarted = state =>
                state.RunId == committedChildRunId
                    ? throw new IOException("the lifecycle store cannot record this run as started")
                    : Task.CompletedTask,
        };

        var abandoned = new Provider();
        await using (
            var failing = NewLoop(
                abandoned,
                threadId,
                store,
                null,
                out var firstCts,
                lifecycleStore: crippled
            )
        )
        {
            (await failing.RecoverAsync()).Should().BeTrue();
            await using var firstWatcher = new MessageWatcher(failing);
            _ = failing.RunAsync(firstCts.Token);
            await firstWatcher.WaitForCompletionsAsync(1);
            await firstCts.CancelAsync();
        }

        abandoned
            .CallCount.Should()
            .Be(
                0,
                "talking to the provider is the irreversible half; doing it without the marker means "
                    + "the next process does it again and the model sees the conversation twice"
            );
        (await store.ListRunLifecycleAsync(threadId))
            .Should()
            .NotContain(
                r => r.RunId == committedChildRunId,
                "nothing partial was left behind either — the durable state is exactly what recovery "
                    + "expects to find"
            );

        // The process that comes after, with a store that works.
        var healthy = new Provider();
        var publisher = new RecordingLifecyclePublisher();
        await using var resumed = NewLoop(healthy, threadId, store, publisher, out var cts);
        (await resumed.RecoverAsync()).Should().BeTrue();
        await using var watcher = new MessageWatcher(resumed);
        _ = resumed.RunAsync(cts.Token);

        await watcher.WaitForCompletionsAsync(1);

        healthy.CallCount.Should().Be(1, "the continuation is carried once, here, and only here");
        ResultsFor(healthy.RequestAt(0), "tc_1")
            .Should()
            .ContainSingle("one result, carried once")
            .Which.Should()
            .Be("the-answer");
        publisher
            .Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Where(r => r.Cause.Kind == LifecycleRunCauseKinds.ToolResult)
            .Should()
            .ContainSingle()
            .Which.RunId.Should()
            .Be(committedChildRunId, "the committed name survived the process that could not use it");
        await cts.CancelAsync();
    }

    #endregion

    #region Waking the loop after the resolver is gone

    [Fact]
    public async Task AWakeUpEatenByARecoveredChildRun_StillLeavesTheNextResolutionAbleToWakeTheLoop()
    {
        var store = new InMemoryConversationStore();
        var threadId = $"thread-{Guid.NewGuid():N}";
        _ = await CommitAContinuationThatNeverRanAsync(store, threadId);

        // Request 1 is the recovered continuation, which only has to finish; request 2 is a later
        // user turn that parks on a second call; request 3 is that call's own continuation, and
        // whether it happens at all is the whole test.
        var provider = new Provider().DefersOnCall(2, "tc_late");
        var publisher = new RecordingLifecyclePublisher();
        await using var loop = NewLoop(
            provider,
            threadId,
            store,
            publisher,
            out var cts,
            toolCallIds: ["tc_1", "tc_late"]
        );
        (await loop.RecoverAsync()).Should().BeTrue();
        await using var watcher = new MessageWatcher(loop);

        // Recovery queued the cause and wrote the wake-up before the loop existed. The loop takes
        // causes from the coordinator and never from the channel, so it starts the child run with
        // that wake-up still sitting in the queue — and the run's own between-turn poll is what
        // drains it. No timing arranges this; it is what restart recovery always looks like.
        _ = loop.RunAsync(cts.Token);
        await watcher.WaitForCompletionsAsync(1);

        _ = await loop.SendAsync([new TextMessage { Text = "Go", Role = Role.User }]);
        await watcher.WaitForDeferralAsync("tc_late");
        await watcher.WaitForCompletionsAsync(2);

        // The loop is now idle on an empty queue. Nothing but this resolution's own wake-up will
        // ever stir it again, and causes are not written to the queue themselves — so a wake-up
        // suppressed by a flag the run above forgot to clear is a loop that sleeps forever on a
        // continuation it has already committed to running.
        (await loop.TryResolveToolCallAsync("tc_late", "late-answer"))
            .Should()
            .Be(ResolveToolCallOutcome.Resolved);

        await watcher.WaitForCompletionsAsync(3);

        var children = publisher
            .Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Where(r => r.Cause.Kind == LifecycleRunCauseKinds.ToolResult)
            .ToList();
        children
            .Select(c => c.Cause.ToolCallId)
            .Should()
            .BeEquivalentTo(["tc_1", "tc_late"], "each result is owed exactly one continuation");
        children.Select(c => c.RunId).Should().OnlyHaveUniqueItems();
        provider
            .CallCount.Should()
            .Be(3, "the recovered continuation, the user turn, and the continuation it earned");
        (await LoadResultsAsync(store, threadId))
            .Should()
            .NotContain(m => m.IsDeferred, "both calls were answered and both answers were carried");
        await cts.CancelAsync();
    }

    [Fact]
    public async Task CancellingTheResolverRightAfterItCommits_StillRunsTheCauseExactlyOnce()
    {
        var store = new SteerableLifecycleStore(new InMemoryConversationStore());
        var provider = new Provider("tc_a", "tc_b", "tc_c");
        var publisher = new RecordingLifecyclePublisher();
        var threadId = $"thread-{Guid.NewGuid():N}";

        // A queue of one. Any bounded queue would do; one just makes "full" reachable in a single
        // send, so the backpressure the wake-up has to survive is arranged rather than hoped for.
        await using var loop = new MultiTurnAgentLoop(
            provider.Mock.Object,
            DeferringRegistry(["tc_a", "tc_b", "tc_c"]),
            threadId,
            inputChannelCapacity: 1,
            store: new InMemoryConversationStore(),
            lifecycleServices: new MultiTurnLifecycleServices
            {
                Publisher = publisher,
                LifecycleStore = store,
            }
        );
        using var cts = new CancellationTokenSource();
        await using var watcher = new MessageWatcher(loop);
        _ = loop.RunAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "Go", Role = Role.User }]);
        await watcher.WaitForCompletionsAsync(1);

        // Hold the loop inside the first child run. It blocks on the loop's own thread, which is what
        // lets the next resolution's wake-up be written while nothing is draining the queue.
        var childStarted = NewGate();
        var releaseChild = NewGate();
        store.BeforeRunStarted = async state =>
        {
            if (state.CauseToolCallId == "tc_a")
            {
                _ = childStarted.TrySetResult(true);
                await releaseChild.Task;
            }
        };

        await loop.ResolveToolCallAsync("tc_a", "result-a");
        await childStarted.Task.WaitAsync(Patience);

        // Fill the queue, so the wake-up for tc_b cannot be written until the loop drains.
        _ = await loop.SendAsync(
            [NotifyMessage.Create(NotifyKinds.SubAgentCompletion, label: "filler")]
        );

        using var resolver = new CancellationTokenSource();
        var committed = await loop.TryResolveToolCallAsync("tc_b", "result-b", ct: resolver.Token);
        committed.Should().Be(ResolveToolCallOutcome.Resolved);

        // The delivery is over and its caller is gone — a webhook handler returning, a request
        // completing. The cause is already committed; the only thing still owed to it is a nudge.
        await resolver.CancelAsync();

        _ = releaseChild.TrySetResult(true);
        await watcher.WaitForCompletionsAsync(3);
        await watcher.WaitForNotifyAsync("filler");

        await loop.ResolveToolCallAsync("tc_c", "result-c");
        await watcher.WaitForCompletionsAsync(4);

        var children = publisher
            .Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Where(r => r.Cause.Kind == LifecycleRunCauseKinds.ToolResult)
            .ToList();
        children.Select(c => c.Cause.ToolCallId).Should().BeEquivalentTo(["tc_a", "tc_b", "tc_c"]);
        children.Select(c => c.RunId).Should().OnlyHaveUniqueItems("each result is carried once");
        provider
            .CallCount.Should()
            .Be(
                2,
                "the opening turn and the continuation that cleared the last outstanding call — a "
                    + "wake-up lost with the resolver's token would have left the loop asleep on a "
                    + "queued cause instead"
            );
        await cts.CancelAsync();
    }

    #endregion

    #region What a store that cannot name a child run is told

    [Fact]
    public async Task ALateChildRunNoRecordNames_IsReportedAsLostRecoverability_NotAsAContradiction()
    {
        // A store that accepts deferrals and keeps none — a host that restored deferred history the
        // store itself never saw. The resolution write then reports NotFound, which the loop treats
        // as ordinary and which skips the attach entirely, so the child run the parking race mints
        // afterwards is left with no record to attach itself to. This is the only way the late
        // attach can find nothing, and it is a normal deployment rather than a fault.
        var logger = new RecordingLogger();
        await using var race = await ParkRace.StartAsync(
            configure: s => s.ForgetsDeferrals = true,
            logger: logger
        );

        (await race.ReleaseAndFinishAsync()).Should().Be(ResolveToolCallOutcome.Resolved);
        await race.Watcher.WaitForCompletionsAsync(2);

        logger
            .At(LogLevel.Error)
            .Should()
            .BeEmpty(
                "nothing contradicts anything here: the missing record the resolution write reports "
                    + "as normal is the same one the attach cannot find, and calling that an error "
                    + "trains an operator to ignore the case that is one"
            );
        logger
            .At(LogLevel.Warning)
            .Should()
            .ContainSingle(m =>
                m.Contains("tc_race", StringComparison.Ordinal)
                && m.Contains("no resolved entry", StringComparison.Ordinal)
            );
        race.Publisher.Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Should()
            .ContainSingle(
                r => r.Cause.ToolCallId == "tc_race",
                "only recoverability was lost; the continuation itself still starts"
            );
    }

    [Fact]
    public async Task ALateChildRunTheRecordNamesDifferently_IsReportedAsTheContradictionItIs()
    {
        var logger = new RecordingLogger();
        await using var race = await ParkRace.StartAsync(
            configure: s =>
            {
                s.ForgetsDeferrals = true;
                s.AttachReturns = _ => Task.FromResult<string?>("child-another-process-committed");
            },
            logger: logger
        );

        (await race.ReleaseAndFinishAsync()).Should().Be(ResolveToolCallOutcome.Resolved);
        await race.Watcher.WaitForCompletionsAsync(2);

        logger
            .At(LogLevel.Error)
            .Should()
            .ContainSingle(
                m =>
                    m.Contains("child-another-process-committed", StringComparison.Ordinal)
                    && m.Contains("tc_race", StringComparison.Ordinal),
                "a record naming some other run for this result is a real disagreement about which "
                    + "run carries it, and that is what this severity is reserved for"
            );
    }

    [Fact]
    public async Task AStoreFromBeforeChildRunNamingExisted_CarriesTheResolutionRatherThanStallingOnIt()
    {
        // The compatibility claim is made by LegacyLifecycleStore compiling at all: it implements
        // every member the interface had before child-run naming and none of the one it gained.
        await using var race = await ParkRace.StartAsync(wrap: s => new LegacyLifecycleStore(s));

        var outcome = await race.ReleaseAndFinishAsync();

        // "Store failed" is an instruction to deliver again, so deliver again — the way a
        // redelivering transport would. An old store is the same answer every time, so a retry that
        // cannot make progress is not a retry: by this point the resolution is durably committed,
        // and refusing it forever would strand the result with history never told about it.
        var attempts = 1;
        while (outcome == ResolveToolCallOutcome.StoreFailed && attempts < 5)
        {
            attempts++;
            outcome = await race.Loop.TryResolveToolCallAsync("tc_race", ParkRace.Answer);
        }

        outcome
            .Should()
            .Be(
                ResolveToolCallOutcome.Resolved,
                "a store that will never name child runs is a fact about the deployment, not a "
                    + "failure of this delivery — the resolution proceeds without recoverability "
                    + "rather than being refused on every attempt for the rest of time"
            );
        attempts.Should().Be(1, "nothing about an old store gets better by asking it twice");

        var results = await race.ResultsAsync();
        var resolved = results.Single(m => m.ToolCallId == "tc_race");
        resolved.IsDeferred.Should().BeFalse("the result reached history");
        resolved.Result.Should().Be(ParkRace.Answer);
        (await race.Loop.GetDeferredToolCallsAsync())
            .Should()
            .NotContain(
                p => p.ToolCallId == "tc_race",
                "the call is no longer outstanding, because it was actually resolved"
            );

        await race.Watcher.WaitForCompletionsAsync(2);
        race.Publisher.Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Should()
            .ContainSingle(
                r => r.Cause.ToolCallId == "tc_race",
                "and the continuation it could not name still runs here, exactly once"
            );
    }

    [Fact]
    public async Task AStoreFromBeforeChildRunNamingExisted_StillCarriesAContinuationItCannotName()
    {
        var logger = new RecordingLogger();
        await using var race = await ParkRace.StartAsync(
            wrap: s => new LegacyLifecycleStore(s),
            configure: s => s.ForgetsDeferrals = true,
            logger: logger
        );

        (await race.ReleaseAndFinishAsync()).Should().Be(ResolveToolCallOutcome.Resolved);
        await race.Watcher.WaitForCompletionsAsync(2);

        // Past the point where refusing is possible, an old store costs this continuation its
        // recoverability and nothing else — a warning about the deployment, not an error about this
        // result, and emphatically not the contradiction message.
        logger.At(LogLevel.Error).Should().BeEmpty();
        logger
            .At(LogLevel.Warning)
            .Should()
            .ContainSingle(m =>
                m.Contains("cannot name child runs", StringComparison.Ordinal)
                && m.Contains("tc_race", StringComparison.Ordinal)
            );
        race.Publisher.Payloads<RunStartedPayload>(LifecycleEventTypes.RunStarted)
            .Should()
            .ContainSingle(
                r => r.Cause.ToolCallId == "tc_race",
                "the result still reaches the conversation in this process"
            );
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Drives a loop to the state this file is about: a resolution claimed while its requesting run
    /// was still going, held inside the durable write, with the run parked by the time it resumes.
    /// </summary>
    /// <remarks>
    /// The window is genuinely narrow in production and impossible to hit by timing here, so it is
    /// built rather than waited for: one tool call defers immediately (giving the test something
    /// resolvable), a second blocks the turn open, and the store's write is gated. Releasing the
    /// second tool call while the first resolution sits in that gate is the race, made deterministic.
    /// </remarks>
    private sealed class ParkRace : IAsyncDisposable
    {
        public const string Answer = "the-answer";

        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<bool> _releaseWrite = NewGate();
        private Task<ResolveToolCallOutcome> _resolving = null!;

        private ParkRace(
            MultiTurnAgentLoop loop,
            InMemoryConversationStore inner,
            SteerableLifecycleStore store,
            RecordingLifecyclePublisher publisher,
            string threadId
        )
        {
            Loop = loop;
            Inner = inner;
            Store = store;
            Publisher = publisher;
            ThreadId = threadId;
        }

        public MultiTurnAgentLoop Loop { get; }

        public InMemoryConversationStore Inner { get; }

        public SteerableLifecycleStore Store { get; }

        public RecordingLifecyclePublisher Publisher { get; }

        public MessageWatcher Watcher { get; private set; } = null!;

        public string ThreadId { get; }

        /// <summary>Drives a loop into the race.</summary>
        /// <param name="wrap">
        /// Optional façade placed between the loop and the steerable store — how a test says "the
        /// host wired up a store that predates child-run naming" while keeping the hooks that make
        /// the race deterministic.
        /// </param>
        /// <param name="configure">Applied to the steerable store before anything uses it.</param>
        /// <param name="logger">Optional recorder, for tests whose subject is what gets said.</param>
        public static async Task<ParkRace> StartAsync(
            Func<IRunLifecycleStore, IRunLifecycleStore>? wrap = null,
            Action<SteerableLifecycleStore>? configure = null,
            RecordingLogger? logger = null
        )
        {
            var inner = new InMemoryConversationStore();
            var store = new SteerableLifecycleStore(inner);
            configure?.Invoke(store);
            var publisher = new RecordingLifecyclePublisher();
            var provider = new Provider("tc_race", "tc_holds_the_turn_open");
            var threadId = $"thread-{Guid.NewGuid():N}";
            var hold = NewGate();

            var loop = new MultiTurnAgentLoop(
                provider.Mock.Object,
                DeferringRegistry(
                    ["tc_race", "tc_holds_the_turn_open"],
                    holdId: "tc_holds_the_turn_open",
                    hold: hold.Task
                ),
                threadId,
                store: inner,
                logger: logger,
                lifecycleServices: new MultiTurnLifecycleServices
                {
                    Publisher = publisher,
                    LifecycleStore = wrap == null ? store : wrap(store),
                }
            );

            var race = new ParkRace(loop, inner, store, publisher, threadId);
            race.Watcher = new MessageWatcher(loop);
            _ = loop.RunAsync(race._cts.Token);
            _ = await loop.SendAsync([new TextMessage { Text = "Go", Role = Role.User }]);

            // The placeholder is published once the call is reserved and resolvable, and the turn is
            // still open behind the second call — so a resolution started now is claimed against a
            // live run.
            await race.Watcher.WaitForDeferralAsync("tc_race");

            var writeEntered = NewGate();
            store.BeforeResolve = async writeCt =>
            {
                writeCt.ThrowIfCancellationRequested();
                _ = writeEntered.TrySetResult(true);
                await race._releaseWrite.Task;
            };
            race._resolving = Task.Run(() => loop.TryResolveToolCallAsync("tc_race", Answer));
            await writeEntered.Task.WaitAsync(Patience);

            // Now let the turn finish. Both calls park, including the one being resolved.
            _ = hold.TrySetResult(true);
            await race.Watcher.WaitForCompletionsAsync(1);
            return race;
        }

        /// <summary>Lets the held write finish and reports what the resolution came to.</summary>
        public Task<ResolveToolCallOutcome> ReleaseAndFinishAsync()
        {
            _ = _releaseWrite.TrySetResult(true);
            return _resolving.WaitAsync(Patience);
        }

        public async Task<DeferredToolCallRecord> RecordAsync(string toolCallId)
        {
            var runs = await Inner.ListRunLifecycleAsync(ThreadId);
            return runs.SelectMany(r => r.DeferredToolCalls).Single(d => d.ToolCallId == toolCallId);
        }

        public Task<IReadOnlyList<ToolCallResultMessage>> ResultsAsync() =>
            LoadResultsAsync(Inner, ThreadId);

        public async ValueTask DisposeAsync()
        {
            _ = _releaseWrite.TrySetResult(true);
            await _cts.CancelAsync();
            await Watcher.DisposeAsync();
            await Loop.DisposeAsync();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Parks a call in one process, resolves it in a second that never runs its loop, and abandons
    /// that process — leaving a resolution whose child run is named durably and never started.
    /// </summary>
    /// <returns>The child run id the abandoned process committed to.</returns>
    private static async Task<string> CommitAContinuationThatNeverRanAsync(
        InMemoryConversationStore store,
        string threadId
    )
    {
        var services = new MultiTurnLifecycleServices
        {
            Publisher = NullLifecyclePublisher.Instance,
            LifecycleStore = store,
        };

        await using (var parking = new MultiTurnAgentLoop(
            new Provider("tc_1").Mock.Object,
            DeferringRegistry(["tc_1"]),
            threadId,
            store: store,
            lifecycleServices: services
        ))
        {
            using var cts = new CancellationTokenSource();
            await using var watcher = new MessageWatcher(parking);
            _ = parking.RunAsync(cts.Token);
            _ = await parking.SendAsync([new TextMessage { Text = "Go", Role = Role.User }]);
            await watcher.WaitForCompletionsAsync(1);
            await cts.CancelAsync();
        }

        // The second process takes the result and commits it. Its loop is never started, so the
        // continuation it names is committed and then abandoned — exactly the state a crash between
        // persisting the result and running the child leaves behind.
        await using (var abandoning = new MultiTurnAgentLoop(
            new Provider().Mock.Object,
            DeferringRegistry(["tc_1"]),
            threadId,
            store: store,
            lifecycleServices: services
        ))
        {
            (await abandoning.RecoverAsync()).Should().BeTrue();
            (await abandoning.TryResolveToolCallAsync("tc_1", "the-answer"))
                .Should()
                .Be(ResolveToolCallOutcome.Resolved);
        }

        var runs = await store.ListRunLifecycleAsync(threadId);
        var record = runs.SelectMany(r => r.DeferredToolCalls).Single(d => d.ToolCallId == "tc_1");
        record
            .ChildRunId.Should()
            .NotBeNull("a resolution applied to a parked call names its child run as it commits");
        runs.Should()
            .NotContain(
                r => r.RunId == record.ChildRunId,
                "the point of the fixture is that the named run never started"
            );
        return record.ChildRunId!;
    }

    private static MultiTurnAgentLoop NewLoop(
        Provider provider,
        string threadId,
        InMemoryConversationStore store,
        RecordingLifecyclePublisher? publisher,
        out CancellationTokenSource cts,
        IRunLifecycleStore? lifecycleStore = null,
        IReadOnlyList<string>? toolCallIds = null,
        ILogger<MultiTurnAgentLoop>? logger = null
    )
    {
        cts = new CancellationTokenSource();
        return new MultiTurnAgentLoop(
            provider.Mock.Object,
            DeferringRegistry(toolCallIds ?? ["tc_1"]),
            threadId,
            store: store,
            logger: logger,
            lifecycleServices: new MultiTurnLifecycleServices
            {
                Publisher = publisher ?? (ILifecyclePublisher)NullLifecyclePublisher.Instance,
                LifecycleStore = lifecycleStore ?? store,
            }
        );
    }

    private static TaskCompletionSource<bool> NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string ToolNameFor(string toolCallId) => $"defer_{toolCallId}";

    private static FunctionRegistry DeferringRegistry(
        IReadOnlyList<string> toolCallIds,
        string? holdId = null,
        Task? hold = null
    )
    {
        var registry = new FunctionRegistry();
        foreach (var toolCallId in toolCallIds)
        {
            var blocked = hold != null && toolCallId == holdId;
            _ = registry.AddFunction(
                new FunctionContract
                {
                    Name = ToolNameFor(toolCallId),
                    Description = $"Defers, for {toolCallId}",
                    Parameters = [],
                },
                async (_, _, _) =>
                {
                    if (blocked)
                    {
                        await hold!;
                    }

                    return new ToolHandlerResult.Deferred();
                }
            );
        }

        return registry;
    }

    /// <summary>The tool results a thread actually has on disk, rehydrated from persistence.</summary>
    private static async Task<IReadOnlyList<ToolCallResultMessage>> LoadResultsAsync(
        IConversationStore store,
        string threadId
    )
    {
        var persisted = await store.LoadMessagesAsync(threadId);
        return
        [
            .. MessagePersistenceConverter
                .FromPersistedMessages(persisted)
                .OfType<ToolCallResultMessage>(),
        ];
    }

    /// <summary>Every result carried for <paramref name="toolCallId"/>, however it was framed.</summary>
    private static IReadOnlyList<string> ResultsFor(
        IReadOnlyList<IMessage> request,
        string toolCallId
    ) =>
        [
            .. request
                .OfType<ToolCallResultMessage>()
                .Where(m => m.ToolCallId == toolCallId)
                .Select(m => m.Result),
            .. request
                .OfType<ToolsCallResultMessage>()
                .SelectMany(m => m.ToolCallResults)
                .Where(r => r.ToolCallId == toolCallId)
                .Select(r => r.Result),
        ];

    /// <summary>
    /// A provider that answers nominated calls with deferring tool calls and every other one with
    /// plain text, recording what it was handed each time.
    /// </summary>
    private sealed class Provider
    {
        private readonly Lock _gate = new();
        private readonly List<IReadOnlyList<IMessage>> _requests = [];
        private readonly Dictionary<int, string[]> _defersByCall = [];

        public Provider(params string[] deferOnFirstCall)
        {
            if (deferOnFirstCall.Length > 0)
            {
                _defersByCall[1] = deferOnFirstCall;
            }

            Mock = new Mock<IStreamingAgent>();
            _ = Mock.Setup(a =>
                    a.GenerateReplyStreamingAsync(
                        It.IsAny<IEnumerable<IMessage>>(),
                        It.IsAny<GenerateReplyOptions>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                    (msgs, _, _) =>
                    {
                        string[]? defers;
                        lock (_gate)
                        {
                            _requests.Add([.. msgs]);
                            _ = _defersByCall.TryGetValue(_requests.Count, out defers);
                        }

                        IReadOnlyList<IMessage> reply =
                            defers is { Length: > 0 }
                                ?
                                [
                                    .. defers.Select(id => new ToolCallMessage
                                    {
                                        FunctionName = ToolNameFor(id),
                                        FunctionArgs = "{}",
                                        ToolCallId = id,
                                        Role = Role.Assistant,
                                    }),
                                ]
                                : [new TextMessage { Text = "all done", Role = Role.Assistant }];

                        return Task.FromResult(ToAsyncEnumerable(reply));
                    }
                );
        }

        public Mock<IStreamingAgent> Mock { get; }

        /// <summary>Makes the <paramref name="call"/>-th request answer with deferring tool calls.</summary>
        /// <remarks>
        /// Counting requests rather than runs is deliberate: a test that needs a second deferral is
        /// almost always describing a specific turn — the one after a recovered continuation, say —
        /// and the request index is the only number that names that turn unambiguously.
        /// </remarks>
        public Provider DefersOnCall(int call, params string[] toolCallIds)
        {
            lock (_gate)
            {
                _defersByCall[call] = toolCallIds;
            }

            return this;
        }

        public int CallCount
        {
            get
            {
                lock (_gate)
                {
                    return _requests.Count;
                }
            }
        }

        public IReadOnlyList<IMessage> RequestAt(int index)
        {
            lock (_gate)
            {
                return _requests[index];
            }
        }

        private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
            IEnumerable<IMessage> messages,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            foreach (var msg in messages)
            {
                ct.ThrowIfCancellationRequested();
                yield return msg;
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Turns a loop's published stream into one-shot gates a test can wait on, so progress is
    /// observed rather than slept through.
    /// </summary>
    /// <remarks>
    /// Every signal is retained: a test that asks for one already raised gets a completed task, which
    /// is what keeps the waits free of ordering races against the loop that raises them.
    /// </remarks>
    private sealed class MessageWatcher : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _signals = new(
            StringComparer.Ordinal
        );
        private readonly Lock _gate = new();
        private readonly Task _pump;
        private int _completions;

        public MessageWatcher(MultiTurnAgentLoop loop)
        {
            // Enumerating starts on the calling thread so the subscription is attached before the
            // caller sends anything: a late subscriber gets no replay and would wait forever.
            var messages = loop.SubscribeAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);
            var first = messages.MoveNextAsync();

            _pump = Task.Run(
                async () =>
                {
                    try
                    {
                        for (var has = await first; has; has = await messages.MoveNextAsync())
                        {
                            switch (messages.Current)
                            {
                                case ToolCallResultMessage { IsDeferred: true } deferred
                                    when !string.IsNullOrEmpty(deferred.ToolCallId):
                                    Raise($"deferred:{deferred.ToolCallId}");
                                    break;
                                case RunCompletedMessage completed:
                                    Raise($"completed:{completed.CompletedRunId}");
                                    Raise($"completions:{Interlocked.Increment(ref _completions)}");
                                    break;
                                case NotifyMessage notify:
                                    Raise($"notify:{notify.Label}");
                                    break;
                                default:
                                    break;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancelling the token is how the watcher ends its subscription.
                    }
                    finally
                    {
                        await messages.DisposeAsync();
                    }
                },
                CancellationToken.None
            );
        }

        /// <summary>Waits until the loop has published a deferred placeholder for the call.</summary>
        public Task WaitForDeferralAsync(string toolCallId) => Wait($"deferred:{toolCallId}");

        /// <summary>Waits until <paramref name="count"/> runs have finished, in publication order.</summary>
        public Task WaitForCompletionsAsync(int count) => Wait($"completions:{count}");

        /// <summary>Waits until a notification with this label has been published.</summary>
        public Task WaitForNotifyAsync(string label) => Wait($"notify:{label}");

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _pump;
            }
            catch (OperationCanceledException)
            {
                // Expected: the pump is ended by cancelling it.
            }

            _cts.Dispose();
        }

        private Task Wait(string key) => Signal(key).Task.WaitAsync(Patience);

        private void Raise(string key) => Signal(key).TrySetResult(true);

        private TaskCompletionSource<bool> Signal(string key)
        {
            lock (_gate)
            {
                if (!_signals.TryGetValue(key, out var signal))
                {
                    signal = NewGate();
                    _signals[key] = signal;
                }

                return signal;
            }
        }
    }

    /// <summary>
    /// A lifecycle store a test can step into: it can be made to refuse to name a child run, and it
    /// can hand control back at the two moments this file cares about — inside the resolution write,
    /// and as a run is recorded as started.
    /// </summary>
    private sealed class SteerableLifecycleStore(InMemoryConversationStore inner) : IRunLifecycleStore
    {
        public InMemoryConversationStore Inner => inner;

        /// <summary>When set, naming a child run throws instead of committing.</summary>
        public bool FailAttach { get; set; }

        /// <summary>
        /// When set, deferrals are accepted and dropped — how a store that never saw the deferral
        /// behaves, which is the ordinary source of
        /// <see cref="DeferredResolutionOutcome.NotFound"/>.
        /// </summary>
        public bool ForgetsDeferrals { get; set; }

        /// <summary>When set, replaces what naming a child run reports back.</summary>
        public Func<string, Task<string?>>? AttachReturns { get; set; }

        /// <summary>Runs inside the resolution write, before anything is committed.</summary>
        public Func<CancellationToken, Task>? BeforeResolve { get; set; }

        /// <summary>Runs before a child run is named, with the thread and tool call being named.</summary>
        public Func<string, string, Task>? BeforeAttach { get; set; }

        /// <summary>Runs on the loop's own thread as a run is recorded as started.</summary>
        public Func<RunLifecycleState, Task>? BeforeRunStarted { get; set; }

        public async Task RecordRunStartedAsync(
            RunLifecycleState state,
            CancellationToken ct = default
        )
        {
            if (BeforeRunStarted != null)
            {
                await BeforeRunStarted(state);
            }

            await inner.RecordRunStartedAsync(state, ct);
        }

        public Task<RunLifecycleState?> LoadRunLifecycleAsync(
            string runId,
            CancellationToken ct = default
        ) => inner.LoadRunLifecycleAsync(runId, ct);

        public Task<IReadOnlyList<RunLifecycleState>> ListRunLifecycleAsync(
            string threadId,
            CancellationToken ct = default
        ) => inner.ListRunLifecycleAsync(threadId, ct);

        public Task<IReadOnlyList<RunLifecycleState>> ListNonTerminalRunsAsync(
            string threadId,
            CancellationToken ct = default
        ) => inner.ListNonTerminalRunsAsync(threadId, ct);

        public Task<bool> TryMarkRunTerminalAsync(
            string runId,
            string outcome,
            int turnCount,
            DateTimeOffset terminalAt,
            CancellationToken ct = default
        ) => inner.TryMarkRunTerminalAsync(runId, outcome, turnCount, terminalAt, ct);

        public Task<DeferredToolCallRecord> RecordDeferredToolCallAsync(
            string runId,
            DeferredToolCallRecord record,
            CancellationToken ct = default
        ) =>
            ForgetsDeferrals
                ? Task.FromResult(record)
                : inner.RecordDeferredToolCallAsync(runId, record, ct);

        public async Task<DeferredResolutionOutcome> TryResolveDeferredToolCallAsync(
            string threadId,
            string toolCallId,
            string resolutionFingerprint,
            string? childRunId,
            DateTimeOffset resolvedAt,
            CancellationToken ct = default
        )
        {
            if (BeforeResolve != null)
            {
                await BeforeResolve(ct);
            }

            return await inner.TryResolveDeferredToolCallAsync(
                threadId,
                toolCallId,
                resolutionFingerprint,
                childRunId,
                resolvedAt,
                ct
            );
        }

        public async Task<string?> AttachDeferredChildRunAsync(
            string threadId,
            string toolCallId,
            string childRunId,
            DateTimeOffset attachedAt,
            CancellationToken ct = default
        )
        {
            if (BeforeAttach != null)
            {
                await BeforeAttach(threadId, toolCallId);
            }

            if (FailAttach)
            {
                throw new InvalidOperationException("the store will not name a child run");
            }

            return AttachReturns != null
                ? await AttachReturns(childRunId)
                : await inner.AttachDeferredChildRunAsync(
                    threadId,
                    toolCallId,
                    childRunId,
                    attachedAt,
                    ct
                );
        }
    }

    /// <summary>
    /// A lifecycle store as one written before child-run naming existed: it implements every member
    /// the interface had then and none of the one it gained, so compiling this file at all is the
    /// compatibility claim, and running against it is what that claim is worth.
    /// </summary>
    /// <remarks>
    /// Deliberately no <c>AttachDeferredChildRunAsync</c>. Adding one would delete the only proof
    /// here that an external implementer still builds.
    /// </remarks>
    private sealed class LegacyLifecycleStore(IRunLifecycleStore inner) : IRunLifecycleStore
    {
        public Task RecordRunStartedAsync(RunLifecycleState state, CancellationToken ct = default) =>
            inner.RecordRunStartedAsync(state, ct);

        public Task<RunLifecycleState?> LoadRunLifecycleAsync(
            string runId,
            CancellationToken ct = default
        ) => inner.LoadRunLifecycleAsync(runId, ct);

        public Task<IReadOnlyList<RunLifecycleState>> ListRunLifecycleAsync(
            string threadId,
            CancellationToken ct = default
        ) => inner.ListRunLifecycleAsync(threadId, ct);

        public Task<IReadOnlyList<RunLifecycleState>> ListNonTerminalRunsAsync(
            string threadId,
            CancellationToken ct = default
        ) => inner.ListNonTerminalRunsAsync(threadId, ct);

        public Task<bool> TryMarkRunTerminalAsync(
            string runId,
            string outcome,
            int turnCount,
            DateTimeOffset terminalAt,
            CancellationToken ct = default
        ) => inner.TryMarkRunTerminalAsync(runId, outcome, turnCount, terminalAt, ct);

        public Task<DeferredToolCallRecord> RecordDeferredToolCallAsync(
            string runId,
            DeferredToolCallRecord record,
            CancellationToken ct = default
        ) => inner.RecordDeferredToolCallAsync(runId, record, ct);

        public Task<DeferredResolutionOutcome> TryResolveDeferredToolCallAsync(
            string threadId,
            string toolCallId,
            string resolutionFingerprint,
            string? childRunId,
            DateTimeOffset resolvedAt,
            CancellationToken ct = default
        ) =>
            inner.TryResolveDeferredToolCallAsync(
                threadId,
                toolCallId,
                resolutionFingerprint,
                childRunId,
                resolvedAt,
                ct
            );
    }

    /// <summary>Keeps every log record the loop writes, so severity can be asserted on.</summary>
    /// <remarks>
    /// Severity is the assertion here rather than incidental detail: the difference between "this
    /// thread cannot recover its continuation" and "two records disagree about which run carries a
    /// result" is entirely in how loudly it is said, and an operator paging on errors is the reader.
    /// </remarks>
    private sealed class RecordingLogger : ILogger<MultiTurnAgentLoop>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel Level, string Message)> _records = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Records
        {
            get
            {
                lock (_gate)
                {
                    return [.. _records];
                }
            }
        }

        public IReadOnlyList<string> At(LogLevel level) =>
            [.. Records.Where(r => r.Level == level).Select(r => r.Message)];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (_gate)
            {
                _records.Add((logLevel, formatter(state, exception)));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }

    #endregion
}
