using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace LmMultiTurn.Tests;

/// <summary>
/// Validates the host-supplied <c>InitialThreadId</c> path added by issue #55.
/// Documented precedence: persisted <c>codex_thread_id</c> in
/// <see cref="ThreadMetadata.Properties"/> &gt; <see cref="CodexSdkOptions.InitialThreadId"/>.
/// InitialThreadId only fills the void when no metadata is stored — once any
/// real thread id has been captured (live or persisted), it dominates.
/// </summary>
public class CodexAgentLoopInitialThreadIdTests : LoggingTestBase
{
    public CodexAgentLoopInitialThreadIdTests(ITestOutputHelper output) : base(output) { }

    private const string Seeded = "codex_thread_seeded_from_option";
    private const string Persisted = "codex_thread_persisted_in_store";

    [Fact]
    public async Task FirstRun_WithInitialThreadIdAndEmptyStore_PassesSeedToClient()
    {
        var fakeClient = new FakeCodexClient(
        [
            Event("turn.completed", """
                {"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}
                """),
        ]);

        var options = new CodexSdkOptions { InitialThreadId = Seeded };
        var store = new InMemoryConversationStore();

        await using var loop = new CodexAgentLoop(
            options,
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "initial-thread-empty-store",
            store: store,
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        // RecoverAsync runs but finds no metadata — seed must survive.
        await loop.RecoverAsync(CancellationToken.None);
        await DriveOneRunAsync(loop);

        fakeClient.LastStartOptions.Should().NotBeNull();
        fakeClient.LastStartOptions!.ThreadId.Should().Be(Seeded,
            "with no persisted metadata, InitialThreadId must seed _codexThreadId so " +
            "the first app-server call uses thread/resume against the supplied id");
    }

    [Fact]
    public async Task FirstRun_WithInitialThreadIdAndStoreOverride_StoreWins()
    {
        var fakeClient = new FakeCodexClient(
        [
            Event("turn.completed", """
                {"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}
                """),
        ]);

        var options = new CodexSdkOptions { InitialThreadId = Seeded };
        var store = new InMemoryConversationStore();
        const string threadId = "initial-thread-store-wins";

        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Properties = ImmutableDictionary.CreateRange(
                [
                    new KeyValuePair<string, object>("codex_thread_id", Persisted),
                ]),
            },
            CancellationToken.None);

        await using var loop = new CodexAgentLoop(
            options,
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: threadId,
            store: store,
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        // RecoverAsync must overwrite the seeded value with the persisted thread id.
        await loop.RecoverAsync(CancellationToken.None);
        await DriveOneRunAsync(loop);

        fakeClient.LastStartOptions!.ThreadId.Should().Be(Persisted,
            "persisted codex_thread_id MUST win over InitialThreadId so cold-restart " +
            "callers can still set a fallback without trampling real saved state");
    }

    [Fact]
    public async Task FirstRun_WithoutInitialThreadIdOrStore_PassesNullThreadId()
    {
        var fakeClient = new FakeCodexClient(
        [
            Event("turn.completed", """
                {"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}
                """),
        ]);

        var options = new CodexSdkOptions();
        var store = new InMemoryConversationStore();

        await using var loop = new CodexAgentLoop(
            options,
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "no-seed-no-store",
            store: store,
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        await loop.RecoverAsync(CancellationToken.None);
        await DriveOneRunAsync(loop);

        fakeClient.LastStartOptions!.ThreadId.Should().BeNull(
            "no seed and no persisted metadata means the first call should go through " +
            "thread/start (null ThreadId), not thread/resume");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NullEmptyOrWhitespaceInitialThreadId_IsTreatedAsUnset(string? seed)
    {
        var fakeClient = new FakeCodexClient(
        [
            Event("turn.completed", """
                {"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}
                """),
        ]);

        var options = new CodexSdkOptions { InitialThreadId = seed };
        var store = new InMemoryConversationStore();

        await using var loop = new CodexAgentLoop(
            options,
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "blank-seed",
            store: store,
            clientFactory: (_, _) => fakeClient,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>());

        await loop.RecoverAsync(CancellationToken.None);
        await DriveOneRunAsync(loop);

        fakeClient.LastStartOptions!.ThreadId.Should().BeNull(
            "null/empty/whitespace seeds must not propagate as a real thread id");
    }

    private static async Task DriveOneRunAsync(CodexAgentLoop loop)
    {
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput(
            [new TextMessage { Role = Role.User, Text = "hello" }]);

        var executeTask = Task.Run(async () =>
        {
            await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token))
            {
            }
        });

        await executeTask.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
    }

    private static CodexTurnEventEnvelope Event(string name, string json)
    {
        var element = JsonDocument.Parse(json).RootElement.Clone();
        return new CodexTurnEventEnvelope
        {
            Type = name,
            Event = element,
            RequestId = Guid.NewGuid().ToString("N"),
            ThreadId = null,
        };
    }

    private sealed class FakeCodexClient : ICodexSdkClient
    {
        private readonly IReadOnlyList<CodexTurnEventEnvelope> _events;

        public FakeCodexClient(IReadOnlyList<CodexTurnEventEnvelope> events)
        {
            _events = events;
        }

        public CodexBridgeInitOptions? LastStartOptions { get; private set; }

        public string? LastRunInput { get; private set; }

        public bool IsRunning { get; private set; }

        public string? CurrentCodexThreadId { get; private set; }

        public string? CurrentTurnId { get; private set; }

        public string DependencyState => "ready";

        public void ConfigureDynamicToolExecutor(
            Func<CodexDynamicToolCallRequest, CancellationToken, Task<CodexDynamicToolCallResponse>>? executor)
        {
        }

        public Task StartOrResumeThreadAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
        {
            IsRunning = true;
            CurrentCodexThreadId = options.ThreadId;
            LastStartOptions = options;
            return Task.CompletedTask;
        }

        public Task EnsureStartedAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
        {
            return StartOrResumeThreadAsync(options, ct);
        }

        public async IAsyncEnumerable<CodexTurnEventEnvelope> RunStreamingAsync(
            string input,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRunInput = input;
            foreach (var item in _events)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task InterruptTurnAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
