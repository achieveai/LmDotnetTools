using System.Collections.Immutable;
using System.Text;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
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
    /// Republishing is what makes attachment deterministic: <see cref="WorkspaceTranscriptMirror.Attach"/>
    /// starts its pump on a worker, so the first publish can legitimately land before the subscription
    /// exists — and a flush is idempotent, so an extra one changes nothing but the count.
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

    private static bool SplicedInto(FakeFileBrowser browser, string path) =>
        browser.Commands.Any(c => c.Arguments.Count == 7 && string.Equals(c.Arguments[6], path, StringComparison.Ordinal));

    // ---------------------------------------------------------------- tests

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

        // No spawn call — this process only ever sees the notification.
        await agent.PublishAsync(new NotifyMessage { NotifyKind = NotifyKinds.SubAgentCompletion });
        await agent.PublishAsync(RunCompleted());

        var expected = $"{AgentsDirectory}/{WorkspaceTranscriptLine.AgentFileLeaf("reviewer", WorkspaceTranscriptLine.ShortId("agent-2"))}{ConversationTranscriptWriter.TranscriptExtension}";
        await WaitForAsync(
            () => SplicedInto(browser, expected),
            "The sub-agent transcript was never written, so the completion notification did not refresh the graph.");
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
