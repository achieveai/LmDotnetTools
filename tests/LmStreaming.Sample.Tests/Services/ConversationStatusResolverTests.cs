using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

public class ConversationStatusResolverTests
{
    private const string ThreadId = "thread-1";

    [Theory]
    [InlineData(RunStatus.Queued, ConversationRunStatus.NotStarted)]
    [InlineData(RunStatus.InProgress, ConversationRunStatus.InProgress)]
    [InlineData(RunStatus.Interrupted, ConversationRunStatus.Interrupted)]
    public async Task ResolveByRunIdAsync_MapsEachRunStatus_ToItsConversationRunStatus(
        RunStatus runStatus,
        ConversationRunStatus expected)
    {
        var store = new InMemoryConversationStore();
        await store.UpsertRunLedgerAsync(Entry(ThreadId, "run-1", runStatus, "input-1"));
        var resolver = new ConversationStatusResolver(store, store);

        var result = await resolver.ResolveByRunIdAsync(ThreadId, "run-1");

        result.Should().NotBeNull();
        result!.RunId.Should().Be("run-1");
        result.Status.Should().Be(expected);
        result.Response.Should().BeNull();
    }

    [Fact]
    public async Task ResolveByRunIdAsync_ReturnsNull_WhenRunIdUnknown()
    {
        var store = new InMemoryConversationStore();
        var resolver = new ConversationStatusResolver(store, store);

        var result = await resolver.ResolveByRunIdAsync(ThreadId, "run-missing");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveByRunIdAsync_ReturnsNull_WhenRunBelongsToDifferentThread()
    {
        var store = new InMemoryConversationStore();
        await store.UpsertRunLedgerAsync(Entry("thread-other", "run-1", RunStatus.InProgress, "input-1"));
        var resolver = new ConversationStatusResolver(store, store);

        var result = await resolver.ResolveByRunIdAsync(ThreadId, "run-1");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveByRunIdAsync_Interrupted_StaysInterrupted_AcrossRepeatedPolls()
    {
        var store = new InMemoryConversationStore();
        await store.UpsertRunLedgerAsync(Entry(ThreadId, "run-1", RunStatus.Interrupted, "input-1"));
        var resolver = new ConversationStatusResolver(store, store);

        var first = await resolver.ResolveByRunIdAsync(ThreadId, "run-1");
        var second = await resolver.ResolveByRunIdAsync(ThreadId, "run-1");

        first!.Status.Should().Be(ConversationRunStatus.Interrupted);
        second!.Status.Should().Be(ConversationRunStatus.Interrupted);
    }

    [Fact]
    public async Task ResolveByRunIdAsync_Completed_ReturnsLastTextMessage_AsResponse()
    {
        var store = new InMemoryConversationStore();
        await store.UpsertRunLedgerAsync(Entry(ThreadId, "run-1", RunStatus.Completed, "input-1"));
        var partial = new TextMessage { Text = "partial", Role = Role.Assistant };
        var final = new TextMessage { Text = "final answer", Role = Role.Assistant };
        await store.AppendMessagesAsync(
            ThreadId,
            [
                MessagePersistenceConverter.ToPersistedMessage(partial, ThreadId, "run-1"),
                MessagePersistenceConverter.ToPersistedMessage(final, ThreadId, "run-1"),
            ]);
        var resolver = new ConversationStatusResolver(store, store);

        var result = await resolver.ResolveByRunIdAsync(ThreadId, "run-1");

        result!.Status.Should().Be(ConversationRunStatus.Completed);
        var json = JsonSerializer.Serialize(result.Response);
        json.Should().Contain("final answer");
        json.Should().NotContain("partial");
    }

    [Fact]
    public async Task ResolveByRunIdAsync_Errored_FallsBackToLastMessage_WhenRunProducedNoText()
    {
        var store = new InMemoryConversationStore();
        await store.UpsertRunLedgerAsync(Entry(ThreadId, "run-1", RunStatus.Errored, "input-1"));
        var toolCall = new ToolCallMessage { FunctionName = "search", ToolCallId = "call-1" };
        await store.AppendMessagesAsync(
            ThreadId,
            [MessagePersistenceConverter.ToPersistedMessage(toolCall, ThreadId, "run-1")]);
        var resolver = new ConversationStatusResolver(store, store);

        var result = await resolver.ResolveByRunIdAsync(ThreadId, "run-1");

        result!.Status.Should().Be(ConversationRunStatus.Errored);
        result.Response.Should().NotBeNull();
    }

    [Fact]
    public async Task ResolveByRunIdAsync_Errored_WithOnlyUserInput_ReturnsNullResponse_NotTheUsersOwnPrompt()
    {
        var store = new InMemoryConversationStore();
        await store.UpsertRunLedgerAsync(Entry(ThreadId, "run-1", RunStatus.Errored, "input-1"));
        var userPrompt = new TextMessage { Text = "please do the thing", Role = Role.User };
        await store.AppendMessagesAsync(
            ThreadId,
            [MessagePersistenceConverter.ToPersistedMessage(userPrompt, ThreadId, "run-1")]);
        var resolver = new ConversationStatusResolver(store, store);

        var result = await resolver.ResolveByRunIdAsync(ThreadId, "run-1");

        result!.Status.Should().Be(ConversationRunStatus.Errored);
        result.Response.Should().BeNull("a run that never produced an assistant answer must not echo the user's own prompt back as the response");
    }

    [Fact]
    public async Task ResolveByRunIdAsync_Completed_SkipsThinkingTrace_ReturnsTheActualAnswer()
    {
        var store = new InMemoryConversationStore();
        await store.UpsertRunLedgerAsync(Entry(ThreadId, "run-1", RunStatus.Completed, "input-1"));
        var thinking = new TextMessage { Text = "reasoning about the request...", Role = Role.Assistant, IsThinking = true };
        var answer = new TextMessage { Text = "final answer", Role = Role.Assistant, IsThinking = false };
        await store.AppendMessagesAsync(
            ThreadId,
            [
                MessagePersistenceConverter.ToPersistedMessage(thinking, ThreadId, "run-1"),
                MessagePersistenceConverter.ToPersistedMessage(answer, ThreadId, "run-1"),
            ]);
        var resolver = new ConversationStatusResolver(store, store);

        var result = await resolver.ResolveByRunIdAsync(ThreadId, "run-1");

        result!.Status.Should().Be(ConversationRunStatus.Completed);
        var json = JsonSerializer.Serialize(result.Response);
        json.Should().Contain("final answer");
        json.Should().NotContain("reasoning about the request");
    }

    [Fact]
    public async Task ResolveByInputIdAsync_ResolvesViaLedgerEntry_WhenInputIdFoldedIntoRun()
    {
        var store = new InMemoryConversationStore();
        await store.UpsertRunLedgerAsync(Entry(ThreadId, "run-1", RunStatus.InProgress, "input-1"));
        var resolver = new ConversationStatusResolver(store, store);

        var result = await resolver.ResolveByInputIdAsync(ThreadId, "input-1");

        result.Should().NotBeNull();
        result!.RunId.Should().Be("run-1");
        result.Status.Should().Be(ConversationRunStatus.InProgress);
    }

    [Fact]
    public async Task ResolveByInputIdAsync_ReturnsNotStarted_WhenAcceptedButNotYetDrainedIntoARun()
    {
        var store = new InMemoryConversationStore();
        await store.RecordAcceptedInputAsync(ThreadId, "input-1", DateTimeOffset.UtcNow);
        var resolver = new ConversationStatusResolver(store, store);

        var result = await resolver.ResolveByInputIdAsync(ThreadId, "input-1");

        result.Should().NotBeNull();
        result!.RunId.Should().BeNull();
        result.Status.Should().Be(ConversationRunStatus.NotStarted);
        result.Response.Should().BeNull();
    }

    [Fact]
    public async Task ResolveByInputIdAsync_ReturnsNull_WhenInputIdWasNeverAccepted()
    {
        var store = new InMemoryConversationStore();
        await store.UpsertRunLedgerAsync(Entry(ThreadId, "run-1", RunStatus.Completed, "input-other"));
        var resolver = new ConversationStatusResolver(store, store);

        var result = await resolver.ResolveByInputIdAsync(ThreadId, "input-unknown");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveByInputIdAsync_InputsFoldedIntoSameRun_ResolveToSharedRunAndResponse()
    {
        var store = new InMemoryConversationStore();
        await store.UpsertRunLedgerAsync(Entry(ThreadId, "run-1", RunStatus.Completed, "input-1", "input-2"));
        var text = new TextMessage { Text = "shared answer", Role = Role.Assistant };
        await store.AppendMessagesAsync(
            ThreadId,
            [MessagePersistenceConverter.ToPersistedMessage(text, ThreadId, "run-1")]);
        var resolver = new ConversationStatusResolver(store, store);

        var first = await resolver.ResolveByInputIdAsync(ThreadId, "input-1");
        var second = await resolver.ResolveByInputIdAsync(ThreadId, "input-2");

        first!.RunId.Should().Be("run-1");
        second!.RunId.Should().Be("run-1");
        first.Status.Should().Be(second.Status);
        JsonSerializer.Serialize(first.Response).Should().Be(JsonSerializer.Serialize(second.Response));
    }

    [Fact]
    public async Task ResolveByInputIdAsync_InputsInDistinctRuns_ResolveIndependently()
    {
        var store = new InMemoryConversationStore();
        await store.UpsertRunLedgerAsync(Entry(ThreadId, "run-1", RunStatus.Completed, "input-1"));
        await store.UpsertRunLedgerAsync(Entry(ThreadId, "run-2", RunStatus.InProgress, "input-2"));
        var text = new TextMessage { Text = "first run answer", Role = Role.Assistant };
        await store.AppendMessagesAsync(
            ThreadId,
            [MessagePersistenceConverter.ToPersistedMessage(text, ThreadId, "run-1")]);
        var resolver = new ConversationStatusResolver(store, store);

        var first = await resolver.ResolveByInputIdAsync(ThreadId, "input-1");
        var second = await resolver.ResolveByInputIdAsync(ThreadId, "input-2");

        first!.RunId.Should().Be("run-1");
        first.Status.Should().Be(ConversationRunStatus.Completed);
        second!.RunId.Should().Be("run-2");
        second.Status.Should().Be(ConversationRunStatus.InProgress);
        first.RunId.Should().NotBe(second.RunId);
    }

    private static RunLedgerEntry Entry(
        string threadId,
        string runId,
        RunStatus status,
        params string[] inputIds)
    {
        var now = DateTimeOffset.UtcNow;
        return new RunLedgerEntry(threadId, runId, status, inputIds, now, now);
    }
}
