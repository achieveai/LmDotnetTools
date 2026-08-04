using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Tests for <see cref="WorkspaceTranscriptMirror"/> — the piece that ties the writer, the flush
/// scheduler and a live agent subscription together (#251).
/// </summary>
/// <remarks>
/// <para>
/// These drive a <b>real <see cref="MultiTurnAgentBase"/></b> rather than a stub subscriber. The behaviour
/// under test that matters most — a subscription the agent silently DROPS when its bounded output channel
/// fills — exists only in that base class, and a hand-written fake that ends its enumeration on command
/// would prove the test's own fake works, not that the mirror survives the real drop.
/// </para>
/// <para>
/// Background work is awaited by polling a bounded deadline, never by sleeping for a guessed duration: the
/// mirror's whole point is that the subscriber hot path does no I/O, so every effect it produces is
/// asynchronous by design. The one place a fixed delay appears is the <i>quiescence</i> helper, where the
/// claim being made is "nothing further happened" — which has no edge to wait on.
/// </para>
/// </remarks>
public sealed class WorkspaceTranscriptMirrorTests
{
    private const string ThreadId = "conv-mirror";
    private const string WorkspaceId = "ws-1";
    private const string Title = "Mirror Test";
    private const string OtherThreadId = "conv-other";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>How long "nothing else happened" is observed for before it is believed.</summary>
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(250);

    private static readonly string ShortThreadId = WorkspaceTranscriptLine.ShortId(ThreadId);

    private static readonly string Leaf = WorkspaceTranscriptLine.MainFileLeaf(Title, ShortThreadId);

    private static readonly string MainPath =
        $"{ConversationTranscriptWriter.TranscriptDirectory}/{Leaf}{ConversationTranscriptWriter.TranscriptExtension}";

    private static readonly string AgentsDirectory =
        $"{ConversationTranscriptWriter.TranscriptDirectory}/{Leaf}{ConversationTranscriptWriter.AgentsDirectorySuffix}";

    private static readonly string TempPath =
        $"{ConversationTranscriptWriter.TempDirectory}/{ShortThreadId}{ConversationTranscriptWriter.TempExtension}";

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// Forwarding store that counts flushes. The writer reads the conversation's metadata exactly once per
    /// flush attempt, before it has decided anything, so that call is the only signal that counts every
    /// attempt including the ones that write nothing.
    /// </summary>
    private sealed class FlushCountingStore(IConversationStore inner) : IConversationStore
    {
        private int _flushes;

        public int FlushCount => Volatile.Read(ref _flushes);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default)
        {
            if (string.Equals(threadId, ThreadId, StringComparison.Ordinal))
            {
                _ = Interlocked.Increment(ref _flushes);
            }

            return inner.LoadMetadataAsync(threadId, ct);
        }

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default) => inner.AppendMessagesAsync(threadId, messages, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default) => inner.LoadMessagesAsync(threadId, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default) => inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task SaveMetadataAsync(
            string threadId,
            ThreadMetadata metadata,
            CancellationToken ct = default) => inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default) => inner.UpdateMetadataAsync(threadId, update, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            CancellationToken ct = default) => inner.ListThreadsAsync(limit, offset, ct);
    }

    /// <summary>
    /// Forwards everything to a real <see cref="PublishingAgent"/> and records the managed thread that
    /// pulls the FIRST <c>MoveNextAsync</c> of the output enumeration.
    /// </summary>
    /// <remarks>
    /// That pull is the moment <c>MultiTurnAgentBase.SubscribeAsync</c> registers the subscriber under
    /// its replay lock — the iterator's prologue runs synchronously, on whichever thread pulls it — so
    /// the recorded id answers "was the subscription live before <c>Attach</c> returned?" without
    /// depending on any timing. A subclass could not observe this: <see cref="PublishingAgent"/> is
    /// sealed and <c>SubscribeAsync</c> is not virtual, so the probe has to be a decorator.
    /// </remarks>
    private sealed class SubscriptionProbeAgent(PublishingAgent inner) : IMultiTurnAgent
    {
        private int _subscribeThreadId;

        /// <summary>Zero until something has started enumerating.</summary>
        public int SubscribeThreadId => Volatile.Read(ref _subscribeThreadId);

        public string? CurrentRunId => inner.CurrentRunId;

        public string ThreadId => inner.ThreadId;

        public bool IsRunning => inner.IsRunning;

        public async IAsyncEnumerable<IMessage> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Volatile.Write(ref _subscribeThreadId, Environment.CurrentManagedThreadId);

            await foreach (var message in inner.SubscribeAsync(ct).ConfigureAwait(false))
            {
                yield return message;
            }
        }

        public ValueTask<SendReceipt> SendAsync(
            List<IMessage> messages,
            string? inputId = null,
            string? parentRunId = null,
            CancellationToken ct = default) => inner.SendAsync(messages, inputId, parentRunId, ct);

        public ValueTask<SendReceipt?> TrySendAsync(
            List<IMessage> messages,
            string? inputId = null,
            string? parentRunId = null,
            CancellationToken ct = default) => inner.TrySendAsync(messages, inputId, parentRunId, ct);

        public IAsyncEnumerable<IMessage> ExecuteRunAsync(UserInput userInput, CancellationToken ct = default) =>
            inner.ExecuteRunAsync(userInput, ct);

        public Task RunAsync(CancellationToken ct = default) => inner.RunAsync(ct);

        public Task StopAsync(TimeSpan? timeout = null) => inner.StopAsync(timeout);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // ---------------------------------------------------------------- helpers


    private static WorkspaceTranscriptMirror CreateMirror(
        IConversationStore store,
        FakeFileBrowser browser,
        Func<string, IMultiTurnAgent?> agentLookup) =>
        new(
            agentLookup,
            store,
            browser,
            new ConversationDescendantScanner(store, NullLogger<ConversationDescendantScanner>.Instance),
            NullLoggerFactory.Instance,
            // Zero: the drop test re-subscribes in a loop and the delay is only there to rate-limit a
            // saturated production conversation, never to sequence anything.
            TimeSpan.Zero,
            // Zero: the writer's stability probe re-reads immediately; these tests drive settling through
            // the store, never through elapsed time.
            TimeSpan.Zero);

    private static Task SeedConversationAsync(IConversationStore store) =>
        store.SaveMetadataAsync(
            ThreadId,
            new ThreadMetadata
            {
                ThreadId = ThreadId,
                LastUpdated = 0,
                Properties = ImmutableDictionary<string, object>
                    .Empty.Add(MultiTurnAgentPool.WorkspacePropertyKey, WorkspaceId)
                    .Add("title", Title),
            });

    private static async Task SeedSubAgentAsync(IConversationStore store, string agentId, string name)
    {
        var childThreadId = SubAgentProvenance.ThreadIdPrefix + agentId;
        await store.SaveMetadataAsync(
            childThreadId,
            new ThreadMetadata
            {
                ThreadId = childThreadId,
                LastUpdated = 0,
                Properties = SubAgentProvenance.Build(
                    ThreadId,
                    new SubAgentSnapshot(
                        agentId,
                        Name: name,
                        TemplateName: "worker",
                        Task: "do the thing",
                        Status: SubAgentStatus.Completed,
                        ThreadId: childThreadId,
                        LastActivityUtc: DateTimeOffset.UnixEpoch,
                        TerminalAtUtc: DateTimeOffset.UnixEpoch)),
            });

        await store.AppendMessagesAsync(childThreadId, [Msg("c1", 1, threadId: childThreadId)]);
    }

    private static PersistedMessage Msg(string id, long timestamp, string threadId = ThreadId) =>
        new()
        {
            Id = id,
            ThreadId = threadId,
            RunId = "run-1",
            GenerationId = "run-1",
            Timestamp = timestamp,
            MessageType = "TextMessage",
            Role = "Assistant",
            MessageJson = $"\"opaque-{id}\"",
        };

    private static string ExpectedAppend(IReadOnlyList<PersistedMessage> all, int skip = 0)
    {
        var lines = WorkspaceTranscriptLine.ChainMessages(
            TranscriptProjection.Normalize(all, excludeReasoning: false));

        return string.Concat(lines.Skip(skip).Select(l => WorkspaceTranscriptLine.Serialize(l) + "\n"));
    }

    private static RunCompletedMessage RunCompleted() => new() { CompletedRunId = "run-1" };

    private static ToolsCallMessage SpawnCall() =>
        new()
        {
            ToolCalls =
            [
                new ToolCall
                {
                    FunctionName = SubAgentToolProvider.SpawnToolName,
                    FunctionArgs = "{}",
                    ToolCallId = "call-1",
                },
            ],
        };

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Publishes run-completions until the mirror has flushed at least <paramref name="flushes"/> times.
    /// A flush is idempotent, so an extra one changes nothing but the count — which is what lets a test
    /// ask for "at least N flushes have happened" as a barrier without knowing how many publishes that
    /// takes. It is NOT a workaround for a racy attachment: since
    /// <see cref="WorkspaceTranscriptMirror.Attach"/> registers the subscription before it returns, one
    /// publish is enough, and
    /// <see cref="Attach_RegistersTheSubscription_BeforeItReturns"/> is the test that holds it to that.
    /// </summary>
    private static async Task PublishUntilFlushedAsync(PublishingAgent agent, FlushCountingStore store, int flushes)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (store.FlushCount < flushes)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The mirror never reached {flushes} flush(es).");
            }

            await agent.PublishAsync(RunCompleted());
            await Task.Delay(10);
        }
    }

    /// <summary>Waits until no flush has started for a quiet window, and returns the settled count.</summary>
    private static async Task<int> SettleAsync(FlushCountingStore store)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (true)
        {
            var before = store.FlushCount;
            await Task.Delay(QuietWindow);
            if (store.FlushCount == before)
            {
                return before;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The mirror never went quiet.");
            }
        }
    }

    /// <summary>
    /// The bytes of the most recent STAGED append. Selected by path rather than by position because the
    /// writer also writes the <c>.gitignore</c>, after the first successful append.
    /// </summary>
    private static string? LastPayload(FakeFileBrowser browser)
    {
        for (var i = browser.Writes.Count - 1; i >= 0; i--)
        {
            if (string.Equals(browser.Writes[i].Path, TempPath, StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetString(browser.Writes[i].Bytes);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the flush spliced into <paramref name="path"/>. The splice is selected by the STAGED temp
    /// file it consumes, not by argument count: the watermark probe now takes a per-line character cap as
    /// a third positional, so it has the same arity as the splice and an arity test alone no longer tells
    /// them apart.
    /// </summary>
    private static bool SplicedInto(FakeFileBrowser browser, string path) =>
        browser.Commands.Any(c =>
            c.Arguments.Contains(TempPath, StringComparer.Ordinal)
            && string.Equals(c.Arguments[^1], path, StringComparison.Ordinal));

    private static SandboxCommandResult Ok() =>
        new() { ExitCode = 0, StandardOutput = "", StandardError = "", OperationId = "op" };

    private static SandboxCommandResult Fail() =>
        new() { ExitCode = 1, StandardOutput = "", StandardError = "no space left on device", OperationId = "op" };

    // ---------------------------------------------------------------- tests

    /// <summary>
    /// <b>Attachment must be complete when <c>Attach</c> returns</b>, not merely requested. The
    /// subscription is registered by the first <c>MoveNextAsync</c> of the agent's output enumeration,
    /// and <c>MultiTurnAgentBase</c> runs that prologue synchronously under its replay lock — so pulling
    /// it on the caller's thread is what closes the window. Handing the whole pump to a worker instead
    /// lets <c>Attach</c> return first, and a completion published in that window is lost outright: the
    /// agent's replay buffer is OPENED by a run assignment and CLOSED by the run completion, so a
    /// completion that arrives before the subscriber exists is neither delivered live nor replayed. For a
    /// conversation that runs once and stops — the common shape of a one-shot task — the transcript is
    /// then never written at all.
    /// </summary>
    [Fact]
    public async Task Attach_RegistersTheSubscription_BeforeItReturns()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        PersistedMessage[] only = [Msg("m1", 1)];
        await store.AppendMessagesAsync(ThreadId, only);

        var browser = new FakeFileBrowser();
        await using var inner = new PublishingAgent(ThreadId);
        var agent = new SubscriptionProbeAgent(inner);
        using var mirror = CreateMirror(store, browser, _ => agent);

        var caller = Environment.CurrentManagedThreadId;
        mirror.Attach(agent);

        // Zero would mean nothing has enumerated yet; a pool thread's id would mean the registration was
        // merely queued. Task.Run never inlines onto the caller, so only pulling it directly can match.
        _ = agent.SubscribeThreadId.Should().Be(
            caller,
            "Attach must pull the first MoveNextAsync itself, so the subscription exists before it returns");

        // The consequence, end to end: ONE completion — published exactly once, never in a retry loop —
        // is enough to get this conversation's rows into its workspace.
        await inner.PublishAsync(RunCompleted());
        await WaitForAsync(
            () => SplicedInto(browser, MainPath),
            "A single run completion published after Attach returned never reached the mirror.");
        _ = LastPayload(browser).Should().Be(ExpectedAppend(only));
    }

    /// <summary>
    /// The whole composition, end to end: an attached agent's run completion drives a flush that appends
    /// this conversation's persisted rows into its workspace, and a LATER completion appends only what is
    /// new. Asserting the exact bytes (not a call count) is what makes the <c>uid</c>/<c>parent_uid</c>
    /// chain — the property every reader depends on — actually covered.
    /// </summary>
    [Fact]
    public async Task Attach_MirrorsTheThread_AtEachTurnBoundary()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        PersistedMessage[] first = [Msg("m1", 1), Msg("m2", 2)];
        await store.AppendMessagesAsync(ThreadId, first);

        var browser = new FakeFileBrowser();
        await using var agent = new PublishingAgent(ThreadId);
        using var mirror = CreateMirror(store, browser, _ => agent);

        mirror.Attach(agent);
        await PublishUntilFlushedAsync(agent, store, 1);
        await WaitForAsync(
            () => SplicedInto(browser, MainPath),
            "The first run completion never produced an append into the main transcript.");

        // Staged through the writer's temp file and spliced from there — the payload is picked by that
        // path because the writer also drops a .gitignore after its first successful append.
        _ = LastPayload(browser).Should().Be(ExpectedAppend(first));

        // A second turn: only the new row is appended, because the watermark advanced on the first splice.
        PersistedMessage[] all = [.. first, Msg("m3", 3)];
        await store.AppendMessagesAsync(ThreadId, [all[2]]);
        await agent.PublishAsync(RunCompleted());

        await WaitForAsync(
            () => LastPayload(browser) == ExpectedAppend(all, skip: 2),
            "The second run completion never appended ONLY the new row.");
    }

    /// <summary>
    /// The flush trigger is the turn BOUNDARY, not message traffic. A turn that streams a hundred deltas
    /// must cost exactly one flush — otherwise every conversation pays a gateway round trip per delta, and
    /// the coalescing scheduler is doing nothing but hiding the cost.
    /// </summary>
    [Fact]
    public async Task Attach_SchedulesNoFlush_ForMessagesInsideATurn()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1)]);

        var browser = new FakeFileBrowser();
        await using var agent = new PublishingAgent(ThreadId);
        using var mirror = CreateMirror(store, browser, _ => agent);

        mirror.Attach(agent);
        await PublishUntilFlushedAsync(agent, store, 1);
        var baseline = await SettleAsync(store);

        for (var i = 0; i < 64; i++)
        {
            await agent.PublishAsync(new TextMessage { Text = "delta", Role = Role.Assistant });
        }

        await agent.PublishAsync(RunCompleted());
        await WaitForAsync(
            () => store.FlushCount > baseline,
            "The run completion after the burst never produced a flush.");

        // The burst is fully consumed by now — the pump is strictly ordered, so observing the completion's
        // flush proves every delta ahead of it was observed too.
        _ = (await SettleAsync(store)).Should().Be(baseline + 1);
    }

    /// <summary>
    /// A sub-agent spawned mid-conversation must appear in the transcript. The descendant graph is cached
    /// (an empty result included), so the ONLY thing that makes a later spawn visible is the mirror
    /// noticing the spawn call and telling the writer — without that, this conversation's first, empty
    /// scan would be served forever and the child's file would never be written.
    /// </summary>
    [Fact]
    public async Task Attach_RefreshesTheDescendantGraph_WhenASpawnCallIsObserved()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1)]);

        var browser = new FakeFileBrowser();
        await using var agent = new PublishingAgent(ThreadId);
        using var mirror = CreateMirror(store, browser, _ => agent);

        mirror.Attach(agent);

        // Two flushes, so the FIRST one has certainly completed its (empty) descendant scan before the
        // sub-agent is seeded. Seeding into the window of that scan would let it be found by luck.
        await PublishUntilFlushedAsync(agent, store, 2);
        _ = await SettleAsync(store);
        await SeedSubAgentAsync(store, "agent-1", "researcher");

        await agent.PublishAsync(SpawnCall());
        await agent.PublishAsync(RunCompleted());

        // Named by the sub-agent's OWN id, not by its prefixed thread id.
        var expected = $"{AgentsDirectory}/{WorkspaceTranscriptLine.AgentFileLeaf("researcher", WorkspaceTranscriptLine.ShortId("agent-1"))}{ConversationTranscriptWriter.TranscriptExtension}";
        await WaitForAsync(
            () => SplicedInto(browser, expected),
            "The sub-agent transcript was never written, so the spawn call did not refresh the graph.");
    }

    /// <summary>
    /// The other half of the cache-invalidation contract: a BACKGROUND sub-agent that finishes after its
    /// parent's turn already ended is never announced by a spawn call in this process's message stream —
    /// the only evidence is the completion notification it pushes back. Matching solely on the spawn call
    /// would leave that child with no file at all, behind a fully green suite.
    /// </summary>
    /// <remarks>
    /// <b>This test used to publish a run completion after the notification and therefore proved
    /// nothing.</b> Noticing a notification and SCHEDULING A FLUSH for it are two different steps, and
    /// only the second one writes a file; the trailing completion supplied that second step itself, so the
    /// test passed while the notification path was inert. There is deliberately no completion here — a
    /// background child finishing after its parent's last turn is exactly the case where none is coming.
    /// </remarks>
    [Fact]
    public async Task Attach_RefreshesTheDescendantGraph_WhenACompletionNotificationIsObserved()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1)]);

        var browser = new FakeFileBrowser();
        await using var agent = new PublishingAgent(ThreadId);
        using var mirror = CreateMirror(store, browser, _ => agent);

        mirror.Attach(agent);

        // As above: two flushes, so the empty scan is certainly cached before the child exists.
        await PublishUntilFlushedAsync(agent, store, 2);
        _ = await SettleAsync(store);
        await SeedSubAgentAsync(store, "agent-2", "reviewer");

        // No spawn call and NO run completion — the notification has to carry this on its own.
        await agent.PublishAsync(new NotifyMessage { NotifyKind = NotifyKinds.SubAgentCompletion });

        var expected = $"{AgentsDirectory}/{WorkspaceTranscriptLine.AgentFileLeaf("reviewer", WorkspaceTranscriptLine.ShortId("agent-2"))}{ConversationTranscriptWriter.TranscriptExtension}";
        await WaitForAsync(
            () => SplicedInto(browser, expected),
            "The sub-agent transcript was never written, so the completion notification did not schedule a flush.");
    }

    /// <summary>
    /// A subscriber that cannot keep up is REMOVED from the agent's fan-out and its channel completed, so
    /// its enumeration ends normally with no exception — indistinguishable, to a naive <c>await foreach</c>,
    /// from the conversation having finished. Every later turn of a still-live conversation would then go
    /// unmirrored, silently. The drop here is the real one: a capacity-1 output channel and a burst that
    /// outruns the pump, driven through the shipping <c>PublishToSubscriber</c>.
    /// </summary>
    [Fact]
    public async Task Attach_ReSubscribes_WhenTheAgentSilentlyDropsTheSubscription()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1)]);

        var browser = new FakeFileBrowser();
        await using var agent = new PublishingAgent(ThreadId, outputChannelCapacity: 1);

        // The pool still holds THIS instance, which is what tells the mirror the conversation is live and
        // the enumeration ended because it was dropped rather than torn down.
        using var mirror = CreateMirror(store, browser, _ => agent);

        mirror.Attach(agent);
        await PublishUntilFlushedAsync(agent, store, 1);

        var deadline = DateTime.UtcNow + Deadline;
        while (mirror.ResubscribeCount == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("A burst into a capacity-1 channel never forced a drop.");
            }

            for (var i = 0; i < 64; i++)
            {
                await agent.PublishAsync(new TextMessage { Text = "delta", Role = Role.Assistant });
            }
        }

        _ = mirror.ResubscribeCount.Should().BeGreaterThan(0);

        // Recovery is the point, not the counter: a turn boundary published AFTER the drop must still
        // reach the mirror.
        var before = store.FlushCount;
        await PublishUntilFlushedAsync(agent, store, before + 1);
    }

    /// <summary>
    /// Recovering the subscription is only half of recovery. Everything published during the gap was
    /// DROPPED — including, in general, the run completion that would have triggered a flush — so a
    /// re-subscribe that does not also schedule one leaves the conversation stalled until some unrelated
    /// later turn happens to arrive. For the common case (the drop happens on the burst of the final
    /// turn) no later turn ever comes and the transcript silently ends one turn early.
    /// </summary>
    /// <remarks>
    /// Nothing published here after the baseline can schedule a flush on its own — deltas are
    /// deliberately not a trigger (see
    /// <see cref="Attach_SchedulesNoFlush_ForMessagesInsideATurn"/>) — so the counter moving at all is
    /// attributable to the recovery path and to nothing else.
    /// </remarks>
    [Fact]
    public async Task Attach_SchedulesAFlush_AfterRecoveringADroppedSubscription()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1)]);

        var browser = new FakeFileBrowser();
        await using var agent = new PublishingAgent(ThreadId, outputChannelCapacity: 1);
        using var mirror = CreateMirror(store, browser, _ => agent);

        mirror.Attach(agent);
        await PublishUntilFlushedAsync(agent, store, 1);
        var baseline = await SettleAsync(store);

        var deadline = DateTime.UtcNow + Deadline;
        while (mirror.ResubscribeCount == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("A burst into a capacity-1 channel never forced a drop.");
            }

            for (var i = 0; i < 64; i++)
            {
                await agent.PublishAsync(new TextMessage { Text = "delta", Role = Role.Assistant });
            }
        }

        await WaitForAsync(
            () => store.FlushCount > baseline,
            "Re-subscribing after a drop never scheduled a flush, so whatever was published during the gap stays unmirrored.");
    }

    /// <summary>
    /// A <c>Deferred</c> flush asks to be rescheduled, and that request must be BOUNDED. A workspace whose
    /// writes are failing defers every time, so an unconditional re-schedule is a self-feeding loop: the
    /// drain is a SINGLE loop shared by every conversation, so one permanently-broken conversation spins
    /// it at full speed and every other conversation's flush queues behind it. The bound is what makes the
    /// retry terminate.
    /// </summary>
    /// <remarks>
    /// The bound is a COUNT, with no delay attached, and that is deliberate: a backoff delay inside the
    /// flush would run on that same shared drain loop and stall every other conversation, and a detached
    /// timer is exactly the background machinery this feature is not meant to grow. The budget is per
    /// turn, not per conversation — the second half of this test — so a transient outage cannot silence a
    /// conversation permanently.
    /// </remarks>
    [Fact]
    public async Task Attach_StopsReschedulingADeferredFlush_AfterABoundedNumberOfAttempts()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1)]);

        // Every splice fails, so every flush defers with its watermark unadvanced and asks for another.
        var browser = new FakeFileBrowser
        {
            ExecResult = new SandboxCommandResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "no space left on device",
                OperationId = "op",
            },
        };
        await using var agent = new PublishingAgent(ThreadId);
        using var mirror = CreateMirror(store, browser, _ => agent);

        mirror.Attach(agent);
        await agent.PublishAsync(RunCompleted());

        // The turn's own flush, then exactly MaxDeferredRetries more. Unbounded, the mirror never goes
        // quiet at all and this settles into a timeout instead of a count.
        var expected = 1 + WorkspaceTranscriptMirror.MaxDeferredRetries;
        _ = (await SettleAsync(store)).Should().Be(expected);

        await agent.PublishAsync(RunCompleted());
        _ = (await SettleAsync(store)).Should().Be(2 * expected);
    }

    /// <summary>
    /// The budget is per TRIGGER, and a run completion is not the only trigger. A background sub-agent that
    /// finishes after its parent's last turn arrives as a notification and nothing else — if that path
    /// leaves an exhausted budget in place, the child gets one single attempt at a workspace that was
    /// failing a moment ago, and its transcript is then abandoned with no further trigger coming. Every
    /// independent external trigger starts a fresh generation of retries.
    /// </summary>
    [Fact]
    public async Task Attach_ResetsTheRetryBudget_WhenASubAgentNotificationArrivesAfterItWasSpent()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1)]);

        var browser = new FakeFileBrowser
        {
            ExecResult = new SandboxCommandResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "no space left on device",
                OperationId = "op",
            },
        };
        await using var agent = new PublishingAgent(ThreadId);
        using var mirror = CreateMirror(store, browser, _ => agent);

        mirror.Attach(agent);
        await agent.PublishAsync(RunCompleted());

        var expected = 1 + WorkspaceTranscriptMirror.MaxDeferredRetries;
        _ = (await SettleAsync(store)).Should().Be(expected);

        // A different trigger entirely — and it is owed the same budget the run completion got.
        await agent.PublishAsync(new NotifyMessage { NotifyKind = NotifyKinds.SubAgentCompletion });
        _ = (await SettleAsync(store)).Should().Be(2 * expected);
    }

    /// <summary>
    /// The fan-out is capped per flush, and a capped pass is PROGRESS rather than failure. Counting it
    /// against the bounded retry budget puts a hard ceiling on how many descendants one trigger can ever
    /// reach — <c>MaxDeferredRetries</c> plus one passes times the per-flush cap — and a conversation whose
    /// roster is larger than that ceiling leaves its tail unmirrored, with no error anywhere and no further
    /// trigger coming once the run has completed.
    /// </summary>
    [Fact]
    public async Task Attach_MirrorsEveryDescendant_WhenTheRosterOutrunsTheRetryBudget()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1)]);

        // One more than (1 + MaxDeferredRetries) passes of the default cap can reach.
        var roster = ((1 + WorkspaceTranscriptMirror.MaxDeferredRetries)
            * ConversationTranscriptWriter.DefaultMaxSubAgentFilesPerFlush) + 1;
        for (var i = 0; i < roster; i++)
        {
            await SeedSubAgentAsync(store, $"agent-{i:D2}", $"worker{i:D2}");
        }

        var browser = new FakeFileBrowser();
        await using var agent = new PublishingAgent(ThreadId);
        using var mirror = CreateMirror(store, browser, _ => agent);

        mirror.Attach(agent);
        await agent.PublishAsync(RunCompleted());

        var expectedPaths = Enumerable.Range(0, roster).Select(i =>
            $"{AgentsDirectory}/{WorkspaceTranscriptLine.AgentFileLeaf($"worker{i:D2}", WorkspaceTranscriptLine.ShortId($"agent-{i:D2}"))}{ConversationTranscriptWriter.TranscriptExtension}")
            .ToList();

        await WaitForAsync(
            () => expectedPaths.All(p => SplicedInto(browser, p)),
            "The tail of the descendant roster was never mirrored: the capped fan-out spent the retry budget before it got there.");
    }

    /// <summary>
    /// A descendant whose splice keeps failing must be RETRIED across passes — and the retrying must still
    /// END. These two are one test on purpose: the cheap way to guarantee termination is to mark every
    /// descendant the sweep merely LOOKED at, which loses the failing one silently, and the cheap way to
    /// guarantee the retry is to keep asking while anything is uncovered, which spins forever on a
    /// descendant that can never be written. Only a version that does both passes here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Termination.</b> A pass that failed reports <c>Deferred</c> and is charged to
    /// <see cref="WorkspaceTranscriptMirror.MaxDeferredRetries"/>; a pass that failed on nothing covers its
    /// whole window, and successive windows tile the roster, so at most ceil(roster / cap) consecutive
    /// passes can be failure-free before either everything is covered or the doomed descendant is picked
    /// again and the budget is charged. Hence the bound asserted below. <see cref="SettleAsync"/> is the
    /// real check: a chain that does not terminate never goes quiet, and this fails as a timeout.
    /// </para>
    /// <para>
    /// The doomed descendant is whichever one the first slice reaches first, chosen that way rather than
    /// by name so the test does not encode the scanner's ordering.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Attach_KeepsRetryingADescendantThatFails_AndStillStops()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1)]);

        const int slices = 2;
        var cap = ConversationTranscriptWriter.DefaultMaxSubAgentFilesPerFlush;
        var roster = slices * cap;
        for (var i = 0; i < roster; i++)
        {
            await SeedSubAgentAsync(store, $"agent-{i:D2}", $"worker{i:D2}");
        }

        var browser = new FakeFileBrowser();
        string? doomed = null;
        var attempts = 0;
        browser.ExecuteHandler = command =>
        {
            var destination = command.Arguments[^1];
            if (!command.Arguments.Contains(TempPath, StringComparer.Ordinal)
                || !destination.StartsWith(AgentsDirectory, StringComparison.Ordinal))
            {
                return Ok();
            }

            doomed ??= destination;
            if (!string.Equals(destination, doomed, StringComparison.Ordinal))
            {
                return Ok();
            }

            attempts++;
            return Fail();
        };

        await using var agent = new PublishingAgent(ThreadId);
        using var mirror = CreateMirror(store, browser, _ => agent);

        mirror.Attach(agent);
        await agent.PublishAsync(RunCompleted());

        var settled = await SettleAsync(store);

        _ = attempts.Should().BeGreaterThan(
            1,
            "a descendant whose splice failed was never retried — the sweep marked it covered the moment it picked it, so the later slice's success ended the chain with that transcript missing"
        );
        _ = settled.Should().BeLessThanOrEqualTo(
            (WorkspaceTranscriptMirror.MaxDeferredRetries + 2) * slices,
            "the retry has to be bounded by the failure budget, not by the roster"
        );
    }

    /// <summary>
    /// Recovering a dropped subscription restarts coverage and the retry budget, but the descendant ROSTER
    /// it restarts over is cached — and the cache is only ever invalidated by an announcement the mirror
    /// OBSERVED. Everything published during the gap was dropped, which is precisely the case where the
    /// announcement of a new sub-agent is the thing that went missing. So a child persisted while the
    /// channel was down, and never mentioned again afterwards, is mirrored only if the recovery path forces
    /// a rescan itself.
    /// </summary>
    /// <remarks>
    /// Only the drop-recovery site forces it. The other two triggers fire because a message ARRIVED: a
    /// sub-agent notification already arms a rescan through <c>NoteSubAgentActivity</c>, and a run
    /// completion is not evidence about descendants at all — forcing a full store scan there would put one
    /// on every turn boundary of every conversation, which is the cost the cache exists to avoid.
    /// </remarks>
    [Fact]
    public async Task Attach_RescansTheDescendantRoster_AfterRecoveringADroppedSubscription()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1)]);

        var browser = new FakeFileBrowser();
        await using var agent = new PublishingAgent(ThreadId, outputChannelCapacity: 1);
        using var mirror = CreateMirror(store, browser, _ => agent);

        mirror.Attach(agent);

        // Two flushes, so the EMPTY descendant scan is certainly cached before the child exists.
        await PublishUntilFlushedAsync(agent, store, 2);
        _ = await SettleAsync(store);

        // The child appears while the channel is about to go down, and NOTHING announces it afterwards.
        await SeedSubAgentAsync(store, "agent-2", "reviewer");

        var deadline = DateTime.UtcNow + Deadline;
        while (mirror.ResubscribeCount == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("A burst into a capacity-1 channel never forced a drop.");
            }

            for (var i = 0; i < 64; i++)
            {
                await agent.PublishAsync(new TextMessage { Text = "delta", Role = Role.Assistant });
            }
        }

        var expected = $"{AgentsDirectory}/{WorkspaceTranscriptLine.AgentFileLeaf("reviewer", WorkspaceTranscriptLine.ShortId("agent-2"))}{ConversationTranscriptWriter.TranscriptExtension}";
        await WaitForAsync(
            () => SplicedInto(browser, expected),
            "The sub-agent that appeared during the gap was never mirrored: recovery reused the roster cached before it existed."
        );
    }

    /// <summary>
    /// A fresh trigger clears the retry budget from the SUBSCRIBER thread, while a flush from the previous
    /// generation can still be sitting inside a gateway call on the drain. If that older flush's
    /// <c>Deferred</c> is counted when it finally returns, it lands in the new generation's budget and the
    /// new trigger starts one attempt down — a workspace recovering from an outage then gets fewer retries
    /// exactly when it needs them, and nothing anywhere reports that the budget was spent on a result the
    /// caller had already given up on.
    /// </summary>
    [Fact]
    public async Task Attach_IgnoresTheOutcomeOfASupersededFlush_SoItCannotSpendTheNewBudget()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1)]);

        // Every gateway call fails, so every flush defers and the budget is the only thing that stops it.
        var browser = new FakeFileBrowser();
        using var reached = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var gated = 0;
        browser.ExecuteHandler = command =>
        {
            if (Interlocked.Exchange(ref gated, 1) == 0)
            {
                reached.Set();
                _ = release.Wait(Deadline);
            }

            return Fail();
        };

        await using var agent = new PublishingAgent(ThreadId);
        using var mirror = CreateMirror(store, browser, _ => agent);

        mirror.Attach(agent);
        await agent.PublishAsync(RunCompleted());

        _ = reached.Wait(Deadline).Should().BeTrue("the first flush never reached the gateway, so nothing is in flight to supersede");

        // A brand-new trigger, published while that first flush is still blocked in the sandbox call.
        await agent.PublishAsync(RunCompleted());

        // The subscriber hot path does no I/O — handling this is a lock and a set-add — so the same quiet
        // window the settling helper trusts is far more than it needs. That the drain really is still
        // parked is asserted, not assumed: nothing can have flushed a second time.
        await Task.Delay(QuietWindow);
        _ = store.FlushCount.Should().Be(1, "the gate did not hold, so there was no in-flight flush to supersede");

        release.Set();

        // The superseded flush, then the new generation's own full budget. Counting the superseded
        // Deferred instead costs the new generation its first attempt and settles one lower.
        var expected = 1 + 1 + WorkspaceTranscriptMirror.MaxDeferredRetries;
        _ = (await SettleAsync(store)).Should().Be(expected);
    }

    /// <summary>
    /// A mode switch builds a REPLACEMENT agent for the same threadId. The subscription must move to it,
    /// and the writer must NOT be rebuilt — a fresh writer has no watermark, so it would re-append the
    /// conversation's entire history under the same uids.
    /// </summary>
    [Fact]
    public async Task Attach_MovesTheSubscription_AndKeepsTheWriter_WhenTheAgentIsReplaced()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        PersistedMessage[] first = [Msg("m1", 1)];
        await store.AppendMessagesAsync(ThreadId, first);

        var browser = new FakeFileBrowser();
        await using var original = new PublishingAgent(ThreadId);
        await using var replacement = new PublishingAgent(ThreadId);
        IMultiTurnAgent current = original;
        using var mirror = CreateMirror(store, browser, _ => current);

        mirror.Attach(original);
        await PublishUntilFlushedAsync(original, store, 1);
        await WaitForAsync(
            () => SplicedInto(browser, MainPath),
            "The original agent's run completion never produced an append.");

        current = replacement;
        mirror.Attach(replacement);
        var settled = await SettleAsync(store);

        PersistedMessage[] all = [.. first, Msg("m2", 2)];
        await store.AppendMessagesAsync(ThreadId, [all[1]]);
        await PublishUntilFlushedAsync(replacement, store, settled + 1);
        await WaitForAsync(
            () => LastPayload(browser) == ExpectedAppend(all, skip: 1),
            "The replacement agent's turn did not append ONLY the new row — the writer was rebuilt.");

        // And the original is no longer listened to: its publishes are inert.
        var afterSwitch = await SettleAsync(store);
        await original.PublishAsync(RunCompleted());
        _ = (await SettleAsync(store)).Should().Be(afterSwitch);
    }

    /// <summary>
    /// Eviction stops the mirror and RETAINS the transcript. Deleting it would defeat the feature: a record
    /// written into the workspace is meant to outlive the conversation that produced it, which is exactly
    /// the moment the pool raises <c>ThreadRemoved</c>.
    /// </summary>
    [Fact]
    public async Task Evict_StopsMirroring_AndNeverDeletesTheTranscript()
    {
        var store = new FlushCountingStore(new InMemoryConversationStore());
        await SeedConversationAsync(store);
        await store.AppendMessagesAsync(ThreadId, [Msg("m1", 1)]);

        var browser = new FakeFileBrowser();
        await using var agent = new PublishingAgent(ThreadId);
        using var mirror = CreateMirror(store, browser, _ => agent);

        mirror.Attach(agent);
        await PublishUntilFlushedAsync(agent, store, 1);
        _ = await SettleAsync(store);

        mirror.Evict(ThreadId);
        var afterEvict = await SettleAsync(store);

        await agent.PublishAsync(RunCompleted());
        _ = (await SettleAsync(store)).Should().Be(afterEvict);
        _ = browser
            .Commands.Should()
            .NotContain(c => c.Arguments.Contains("rm") || c.Arguments.Contains("rm -rf"));
        _ = SplicedInto(browser, MainPath).Should().BeTrue();

        // Evicting an unknown thread is a no-op, not a fault: ThreadRemoved fires for conversations this
        // process may never have attached (a restart, an S2S-only thread).
        var evictUnknown = () => mirror.Evict(OtherThreadId);
        _ = evictUnknown.Should().NotThrow();
    }

    /// <summary>
    /// The mirror must be disposable by a SYNCHRONOUS <c>ServiceProvider.Dispose()</c>. A container-tracked
    /// <c>IAsyncDisposable</c>-only singleton makes that call throw <i>"only implements IAsyncDisposable"</i>,
    /// which is how a mirror bug turns into every E2E test failing at host teardown for a reason that looks
    /// unrelated to transcripts. This test fails at COMPILE time if the interface is ever narrowed, and at
    /// run time if disposal starts throwing.
    /// </summary>
    [Fact]
    public async Task Dispose_TearsDownTheHostSynchronously_WithLiveSubscriptions()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store);

        var services = new ServiceCollection();
        _ = services.AddSingleton<IConversationStore>(store);
        _ = services.AddSingleton<IWorkspaceFileBrowser>(new FakeFileBrowser());
        _ = services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        _ = services.AddSingleton(sp => new ConversationDescendantScanner(
            sp.GetRequiredService<IConversationStore>(),
            NullLogger<ConversationDescendantScanner>.Instance));
        _ = services.AddSingleton(sp => new WorkspaceTranscriptMirror(
            _ => null,
            sp.GetRequiredService<IConversationStore>(),
            sp.GetRequiredService<IWorkspaceFileBrowser>(),
            sp.GetRequiredService<ConversationDescendantScanner>(),
            sp.GetRequiredService<ILoggerFactory>()));

        var provider = services.BuildServiceProvider();
        await using var agent = new PublishingAgent(ThreadId);
        var mirror = provider.GetRequiredService<WorkspaceTranscriptMirror>();
        mirror.Attach(agent);
        await agent.PublishAsync(RunCompleted());

        var dispose = provider.Dispose;
        _ = dispose.Should().NotThrow();

        // Idempotent, because host teardown and an explicit dispose can both reach it.
        var again = mirror.Dispose;
        _ = again.Should().NotThrow();
    }
}
