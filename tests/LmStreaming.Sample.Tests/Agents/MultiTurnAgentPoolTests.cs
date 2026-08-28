using System.Collections.Concurrent;
using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmTestUtils;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Agents;

[Collection("EnvironmentVariables")]
public class MultiTurnAgentPoolTests
{
    [Fact]
    public async Task IsRunInProgress_ReturnsFalse_WhenAgentDoesNotExist()
    {
        await using var pool = CreatePool();
        pool.IsRunInProgress("missing-thread").Should().BeFalse();
    }

    [Fact]
    public async Task IsRunInProgress_ReturnsTrue_WhenCurrentRunIdIsSet()
    {
        await using var pool = CreatePool();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent("thread-1", mode);
        agent.CurrentRunId = "run_123";
        agent.IsRunning = true;

        pool.IsRunInProgress("thread-1").Should().BeTrue();
    }

    [Fact]
    public async Task IsRunInProgress_ReturnsFalse_WhenCurrentRunIdIsNull()
    {
        await using var pool = CreatePool();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent("thread-2", mode);
        agent.CurrentRunId = null;

        pool.IsRunInProgress("thread-2").Should().BeFalse();
    }

    [Fact]
    public async Task IsRunInProgress_ReturnsFalse_WhenRunStateIsStale()
    {
        await using var pool = CreatePool();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent("thread-stale", mode);
        agent.CurrentRunId = "run_stale";
        agent.IsRunning = false;

        pool.IsRunInProgress("thread-stale").Should().BeFalse();

        var state = pool.GetRunStateInfo("thread-stale");
        state.IsStale.Should().BeTrue();
        state.CurrentRunId.Should().Be("run_stale");
    }

    [Fact]
    public async Task GetOrCreateAgent_PersistsRequestedProvider_OnFirstCreation()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai", "anthropic"]);
        var store = new InMemoryConversationStore();
        var providerSeen = new List<string>();

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, providerId, _) =>
            {
                providerSeen.Add(providerId);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId));
            },
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-x", mode, requestedProviderId: "openai", requestResponseDumpFileName: null);

        providerSeen.Should().ContainSingle().Which.Should().Be("openai");

        // Persistence is fire-and-forget; allow it to complete before asserting.
        var persisted = await WaitForPersistedProviderAsync(store, "thread-x");
        persisted.Should().Be("openai");
    }

    [Fact]
    public async Task GetOrCreateAgent_PersistsMode_OnFirstCreation()
    {
        // BUG 3: the conversation's chat mode was never persisted, so after a refresh the client had no
        // bound mode to restore and fell back to the default (General Assistant). The mode must be
        // persisted alongside provider/workspace at first creation.
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var store = new InMemoryConversationStore();

        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-mode", mode, requestedProviderId: "test", requestResponseDumpFileName: null);

        var persistedMode = await WaitForPersistedPropertyAsync(store, "thread-mode", "mode");
        persistedMode.Should().Be(mode.Id);
    }

    [Fact]
    public async Task GetOrCreateAgent_PersistsProviderWorkspaceAndMode_Together_OnFirstCreation()
    {
        // The three bindings must ALL survive first creation. Previously provider and workspace were
        // persisted by two concurrent read-modify-write tasks that clobbered each other (measured: the
        // provider was frequently lost), and mode was not persisted at all.
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai"]);
        var store = new InMemoryConversationStore();

        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(
            "thread-bindings",
            mode,
            requestedProviderId: "openai",
            requestResponseDumpFileName: null,
            requestedWorkspaceId: "ws-1"
        );

        (await WaitForPersistedPropertyAsync(store, "thread-bindings", "provider")).Should().Be("openai");
        (await WaitForPersistedPropertyAsync(store, "thread-bindings", "workspace")).Should().Be("ws-1");
        (await WaitForPersistedPropertyAsync(store, "thread-bindings", "mode")).Should().Be(mode.Id);
    }

    [Fact]
    public async Task GetOrCreateAgent_PersistedProviderWins_OverRequested()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai", "anthropic"]);
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            "thread-y",
            new ThreadMetadata
            {
                ThreadId = "thread-y",
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                    MultiTurnAgentPool.ProviderPropertyKey,
                    "anthropic"
                ),
            }
        );

        var providerSeen = new List<string>();
        await using var pool = new MultiTurnAgentPool(
            (threadId, _, providerId, _) =>
            {
                providerSeen.Add(providerId);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId));
            },
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-y", mode, requestedProviderId: "openai", requestResponseDumpFileName: null);

        providerSeen.Should().ContainSingle().Which.Should().Be("anthropic");
    }

    [Fact]
    public async Task GetOrCreateAgent_LogsWarning_WhenRequestedProviderOverriddenByPersisted()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai", "anthropic"]);
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            "thread-override",
            new ThreadMetadata
            {
                ThreadId = "thread-override",
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                    MultiTurnAgentPool.ProviderPropertyKey,
                    "anthropic"
                ),
            }
        );

        var logger = new CapturingLogger<MultiTurnAgentPool>();
        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            registry.ToReal(),
            store,
            logger
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(
            "thread-override",
            mode,
            requestedProviderId: "test",
            requestResponseDumpFileName: null
        );

        var warning = logger
            .Entries.Where(e => e.Level == LogLevel.Warning)
            .Should()
            .ContainSingle(e => e.Message.Contains("anthropic") && e.Message.Contains("test"))
            .Subject;
        warning.Message.Should().Contain("locked");
    }

    [Fact]
    public async Task GetOrCreateAgent_DoesNotWarn_WhenRequestedProviderMatchesPersisted()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "anthropic"]);
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            "thread-match",
            new ThreadMetadata
            {
                ThreadId = "thread-match",
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                    MultiTurnAgentPool.ProviderPropertyKey,
                    "anthropic"
                ),
            }
        );

        var logger = new CapturingLogger<MultiTurnAgentPool>();
        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            registry.ToReal(),
            store,
            logger
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(
            "thread-match",
            mode,
            requestedProviderId: "anthropic",
            requestResponseDumpFileName: null
        );

        logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task GetOrCreateAgent_PersistedJsonElementProviderWins_OverRequested()
    {
        var registry = new FakeProviderRegistry(
            defaultProviderId: "test",
            available: ["test", "codex-mock", "anthropic"]
        );
        var store = new InMemoryConversationStore();
        using var providerDocument = JsonDocument.Parse("\"codex-mock\"");
        var providerElement = providerDocument.RootElement.Clone();
        await store.SaveMetadataAsync(
            "thread-json",
            new ThreadMetadata
            {
                ThreadId = "thread-json",
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                    MultiTurnAgentPool.ProviderPropertyKey,
                    providerElement
                ),
            }
        );

        var providerSeen = new List<string>();
        await using var pool = new MultiTurnAgentPool(
            (threadId, _, providerId, _) =>
            {
                providerSeen.Add(providerId);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId));
            },
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(
            "thread-json",
            mode,
            requestedProviderId: "anthropic",
            requestResponseDumpFileName: null
        );

        providerSeen.Should().ContainSingle().Which.Should().Be("codex-mock");
    }

    [Fact]
    public async Task GetOrCreateAgent_Throws_WhenPersistedProviderUnavailable()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            "thread-z",
            new ThreadMetadata
            {
                ThreadId = "thread-z",
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                    MultiTurnAgentPool.ProviderPropertyKey,
                    "openai"
                ),
            }
        );

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var act = () =>
            pool.GetOrCreateAgent("thread-z", mode, requestedProviderId: null, requestResponseDumpFileName: null);

        act.Should().Throw<ProviderUnavailableException>().Which.ProviderId.Should().Be("openai");
    }

    [Fact]
    public async Task GetOrCreateAgent_Throws_WhenRequestedProviderUnavailable()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var store = new InMemoryConversationStore();

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var act = () =>
            pool.GetOrCreateAgent("thread-q", mode, requestedProviderId: "openai", requestResponseDumpFileName: null);

        act.Should().Throw<ProviderUnavailableException>().Which.ProviderId.Should().Be("openai");
    }

    [Fact]
    public async Task GetOrCreateAgent_FallsBackToDefault_WhenNoRequestedAndNoPersisted()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var store = new InMemoryConversationStore();
        var providerSeen = new List<string>();

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, providerId, _) =>
            {
                providerSeen.Add(providerId);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId));
            },
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-d", mode, requestedProviderId: null, requestResponseDumpFileName: null);

        providerSeen.Should().ContainSingle().Which.Should().Be("test");

        var persisted = await WaitForPersistedProviderAsync(store, "thread-d");
        persisted.Should().Be("test");
    }

    [Fact]
    public async Task GetOrCreateAgent_LegacyConstructor_PassesSentinelToFactory()
    {
        var providerSeen = new List<string>();
        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _) =>
            {
                providerSeen.Add("default-sentinel-observed");
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId));
            },
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-legacy", mode);

        providerSeen.Should().ContainSingle();
    }

    [Fact]
    public async Task GetEffectiveProviderId_ReturnsPersisted_EvenWhenUnavailable()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            "thread-eff",
            new ThreadMetadata
            {
                ThreadId = "thread-eff",
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                    MultiTurnAgentPool.ProviderPropertyKey,
                    "openai"
                ),
            }
        );

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        pool.GetEffectiveProviderId("thread-eff", null).Should().Be("openai");
    }

    [Fact]
    public async Task TryGet_ReturnsExistingAgent_AfterGetOrCreate()
    {
        await using var pool = CreatePool();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var created = pool.GetOrCreateAgent("thread-tryget", mode);

        var success = pool.TryGet("thread-tryget", out var fetched);

        success.Should().BeTrue();
        fetched.Should().BeSameAs(created);
    }

    [Fact]
    public async Task TryGet_ReturnsFalse_WhenThreadIdUnknown()
    {
        await using var pool = CreatePool();

        var success = pool.TryGet("never-created", out var fetched);

        success.Should().BeFalse();
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task TryGet_ReturnsFalse_WhenThreadIdIsEmpty()
    {
        await using var pool = CreatePool();

        // Guards against accidental TryGet("") calls from upstream where a missing/empty
        // sessionId or threadId would otherwise hash to the empty-string slot.
        pool.TryGet(string.Empty, out var fetched).Should().BeFalse();
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task HasArmedWaitAsync_ReturnsFalse_WhenNoAgentPooled()
    {
        await using var pool = CreatePool();

        // Nothing pooled for this threadId → no armed Wait to lose on a switch.
        (await pool.HasArmedWaitAsync("never-created"))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task HasArmedWaitAsync_ReturnsFalse_ForPooledNonLoopAgent()
    {
        // The pooled agent is a FakeMultiTurnAgent, not a MultiTurnAgentLoop, so the concrete
        // `is MultiTurnAgentLoop` downcast in HasArmedWaitAsync fails and the method degrades to false
        // (a non-loop agent exposes no deferred-call inspection). This is the realistic path the
        // controller's switch tests take, which is why their Warning is null.
        await using var pool = CreatePool();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-nonloop", mode);

        (await pool.HasArmedWaitAsync("thread-nonloop")).Should().BeFalse();
    }

    [Fact]
    public async Task HasArmedWaitAsync_ReturnsTrue_WhenPooledLoopHasArmedWait()
    {
        // TRUE path (finding 3c): a real MultiTurnAgentLoop parked on a Wait. HasArmedWaitAsync does a
        // concrete `is MultiTurnAgentLoop` downcast, so only a real loop (not the FakeMultiTurnAgent) can
        // exercise the positive branch. The loop's mock LLM emits a Wait tool call that the registry
        // defers, leaving an armed Wait in the loop's deferred set. The run is driven deterministically
        // via ExecuteRunAsync (it blocks until the run parks — no sleeps), drained by the pool's own
        // background RunAsync pump.
        var waitCall = new ToolCallMessage
        {
            FunctionName = WaitToolProvider.WaitToolName,
            FunctionArgs = "{\"kind\":\"timer\",\"timeout\":\"10m\"}",
            ToolCallId = "tc_wait",
            Role = Role.Assistant,
        };

        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() => Task.FromResult(ToAsyncEnumerable(waitCall)));

        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
            {
                Name = WaitToolProvider.WaitToolName,
                Description = "Parks the run on a timer.",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred())
        );

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _) =>
                new MultiTurnAgentPool.AgentCreationResult(
                    new MultiTurnAgentLoop(
                        mockAgent.Object,
                        registry,
                        threadId,
                        logger: NullLogger<MultiTurnAgentLoop>.Instance
                    )
                ),
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-armed-wait", mode);

        // Drive one run to park it on the Wait. ExecuteRunAsync enqueues the input and yields until the
        // run completes (parked with a deferred Wait), which the pool's background pump processes.
        var userInput = new UserInput([new TextMessage { Text = "wait please", Role = Role.User }]);
        await foreach (var _ in loop.ExecuteRunAsync(userInput))
        {
            // drain
        }

        (await pool.HasArmedWaitAsync("thread-armed-wait")).Should().BeTrue();
    }

    [Fact]
    public async Task HasPendingAskUserQuestionAsync_ReturnsTrue_WhenPooledLoopHasParkedAskUserQuestion_AndFalseAfterResolve()
    {
        // TRUE path for #246's mode/provider hard-block guards: a real MultiTurnAgentLoop parked on
        // AskUserQuestion (registered unconditionally by every loop's constructor). Only a real loop —
        // never the FakeMultiTurnAgent every other pool/controller test uses — can exercise
        // HasPendingAskUserQuestionAsync's true branch, since the method downcasts to MultiTurnAgentLoop
        // and degrades to false otherwise.
        const string toolCallId = "tc_color";
        var askCall = new ToolCallMessage
        {
            FunctionName = AskUserQuestionToolProvider.ToolName,
            FunctionArgs = JsonSerializer.Serialize(
                new
                {
                    context = "Need to know which color to use.",
                    questions = new[]
                    {
                        new
                        {
                            prompt = "Which color?",
                            options = new object[] { new { label = "Red" }, new { label = "Blue" } },
                        },
                    },
                }
            ),
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };

        // The mock must answer only ONCE with the deferred AskUserQuestion. TryResolveToolCallAsync
        // wakes the loop's background pump to run the resolved call's continuation on a task the test
        // does not await directly (ScheduleLoopWake) — if the mock kept returning askCall unconditionally,
        // that continuation would immediately re-defer the SAME tool call id, racing the assertion below
        // and making it flaky depending on whether the background wake beat the test to the check.
        var callCount = 0;
        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) =>
                {
                    IMessage msg =
                        Interlocked.Increment(ref callCount) == 1
                            ? askCall
                            : new TextMessage { Text = "Using blue.", Role = Role.Assistant };
                    return Task.FromResult(ToAsyncEnumerable(msg));
                }
            );

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _) =>
                new MultiTurnAgentPool.AgentCreationResult(
                    new MultiTurnAgentLoop(
                        mockAgent.Object,
                        new FunctionRegistry(),
                        threadId,
                        logger: NullLogger<MultiTurnAgentLoop>.Instance
                    )
                ),
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent("thread-pending-question", mode);

        var userInput = new UserInput([new TextMessage { Text = "Which color should I use?", Role = Role.User }]);
        await foreach (var _ in loop.ExecuteRunAsync(userInput))
        {
            // drain until the run parks on the deferred AskUserQuestion
        }

        (await pool.HasPendingAskUserQuestionAsync("thread-pending-question")).Should().BeTrue();

        var outcome = await loop.TryResolveToolCallAsync(toolCallId, "blue", isError: false);
        outcome.Should().Be(ResolveToolCallOutcome.Resolved);

        (await pool.HasPendingAskUserQuestionAsync("thread-pending-question"))
            .Should()
            .BeFalse("once resolved the call is no longer deferred, so the pending lookup must clear too");
    }

    [Fact]
    public async Task HasPendingAskUserQuestionAsync_ReturnsTrue_AfterRestartRecovery_FromPersistedAskUserQuestion()
    {
        // #1 (restart restoration) + #3 (pool pending lookup) together: a previous process persisted a
        // deferred AskUserQuestion placeholder and then exited/crashed. A freshly-built loop over the
        // SAME store recovers it via RecoverAsync (OnHistoryRestoredAsync rebuilds the in-memory
        // deferred registry from persisted history), and once that loop is registered in the pool,
        // HasPendingAskUserQuestionAsync must see the recovered call exactly as it would a live one.
        //
        // This also exercises the end of that story: resolving the recovered call must wake the loop's
        // background pump and drive the continuation through the provider EXACTLY ONCE — not zero times
        // (the answer would never be delivered) and not more than once (a double-fired continuation would
        // send the provider a duplicate turn for an answer already recorded).
        const string threadId = "thread-restart-question";
        const string runId = "run_prev";
        const string generationId = "gen_prev";
        const string toolCallId = "tc_persisted_question";
        var store = new InMemoryConversationStore();

        var toolCall = new ToolCallMessage
        {
            ToolCallId = toolCallId,
            FunctionName = AskUserQuestionToolProvider.ToolName,
            FunctionArgs =
                "{\"context\":\"ctx\",\"questions\":[{\"prompt\":\"Which?\",\"options\":[{\"label\":\"A\"}]}]}",
            Role = Role.Assistant,
            FromAgent = "test",
            GenerationId = generationId,
            RunId = runId,
        };
        var deferredResult = new ToolCallResultMessage
        {
            ToolCallId = toolCallId,
            ToolName = AskUserQuestionToolProvider.ToolName,
            Result = string.Empty,
            IsDeferred = true,
            DeferredAt = 1_700_000_000_000,
            Role = Role.User,
            GenerationId = generationId,
            RunId = runId,
        };

        await store.AppendMessagesAsync(
            threadId,
            [
                MessagePersistenceConverter.ToPersistedMessage(toolCall, threadId, runId),
                MessagePersistenceConverter.ToPersistedMessage(deferredResult, threadId, runId),
            ]
        );
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = runId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );

        // Nothing before resolution should ever reach the provider — the original call belonged to a
        // process that no longer exists, so this mock's very first invocation IS the continuation.
        var callCount = 0;
        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) =>
                {
                    Interlocked.Increment(ref callCount);
                    IMessage msg = new TextMessage { Text = "Using A.", Role = Role.Assistant };
                    return Task.FromResult(ToAsyncEnumerable(msg));
                }
            );

        await using var pool = new MultiTurnAgentPool(
            (tid, _, _) =>
                new MultiTurnAgentPool.AgentCreationResult(
                    new MultiTurnAgentLoop(
                        mockAgent.Object,
                        new FunctionRegistry(),
                        tid,
                        store: store,
                        logger: NullLogger<MultiTurnAgentLoop>.Instance
                    )
                ),
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent(threadId, mode);

        (await loop.RecoverAsync()).Should().BeTrue();

        (await pool.HasPendingAskUserQuestionAsync(threadId)).Should().BeTrue();

        var outcome = await loop.TryResolveToolCallAsync(toolCallId, "A", isError: false);
        outcome.Should().Be(ResolveToolCallOutcome.Resolved);

        // Resolving wakes the loop in the background; wait for the continuation to actually reach the
        // provider before asserting on it, rather than racing the pump.
        await Wait.UntilAsync(
            () => Volatile.Read(ref callCount) >= 1,
            "resolving the recovered pending question drove the continuation as far as the provider",
            TimeSpan.FromSeconds(5)
        );

        (await pool.HasPendingAskUserQuestionAsync(threadId))
            .Should()
            .BeFalse("once the recovered call is resolved and its continuation has run, nothing is deferred any more");

        // Give any spurious second wake-up time to land before asserting the count is exact.
        await Task.Delay(200);
        Volatile
            .Read(ref callCount)
            .Should()
            .Be(
                1,
                "resolving the recovered pending question must drive exactly one continuation run, not zero and not more than one"
            );
    }

    [Fact]
    public async Task HasPendingAskUserQuestionAsync_ReturnsTrue_WhenDirectChildHasParkedAskUserQuestion()
    {
        // #246: recreating the primary agent (a mode/provider switch) disposes its ENTIRE live
        // descendant tree, not just the primary's own deferred calls. A direct child's unresolved
        // AskUserQuestion must hard-block the switch exactly as the primary's own pending question
        // already does — even though the PRIMARY itself has nothing deferred. True nested
        // (grandchild) descendants cannot be produced via the normal Agent-tool spawn path today:
        // CreateSubAgentAsync never gives a spawned child its own SubAgentOptions, so a plain
        // sub-agent can never itself spawn further sub-agents. The recursive
        // HasPendingAskUserQuestionInDescendantsAsync walk exists to stay correct if that topology
        // ever changes, but a grandchild scenario is not constructible through this test.
        const string threadId = "thread-child-pending-question";
        const string toolCallId = "tc_child_color";
        const string templateName = "asker";

        var childAskCall = new ToolCallMessage
        {
            FunctionName = AskUserQuestionToolProvider.ToolName,
            FunctionArgs = JsonSerializer.Serialize(
                new
                {
                    context = "Need to know which color to use.",
                    questions = new[]
                    {
                        new
                        {
                            prompt = "Which color?",
                            options = new object[] { new { label = "Red" }, new { label = "Blue" } },
                        },
                    },
                }
            ),
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };

        var childAgent = new Mock<IStreamingAgent>();
        childAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.FromResult(ToAsyncEnumerable(childAskCall)));

        var parentAgent = new Mock<IStreamingAgent>();
        parentAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.FromResult(ToAsyncEnumerable()));

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                [templateName] = new SubAgentTemplate
                {
                    Name = templateName,
                    SystemPrompt = "You ask the user a clarifying question.",
                    AgentFactory = () => childAgent.Object,
                },
            },
            MaxConcurrentSubAgents = 5,
        };

        await using var pool = new MultiTurnAgentPool(
            (tid, _, _) =>
                new MultiTurnAgentPool.AgentCreationResult(
                    new MultiTurnAgentLoop(
                        parentAgent.Object,
                        new FunctionRegistry(),
                        tid,
                        subAgentOptions: subAgentOptions,
                        logger: NullLogger<MultiTurnAgentLoop>.Instance
                    )
                ),
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var loop = (MultiTurnAgentLoop)pool.GetOrCreateAgent(threadId, mode);

        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var spawnJson = await loop.SubAgentManager!.SpawnAsync(
            templateName,
            "ask the user",
            name: "asker",
            runInBackground: true
        );
        using var doc = JsonDocument.Parse(spawnJson);
        var agentId = doc.RootElement.GetProperty("agent_id").GetString()!;

        await WaitUntilChildAwaitingQuestionAsync(loop.SubAgentManager!, agentId, ct.Token);

        // The primary itself has nothing deferred...
        (await loop.GetDeferredToolCallsAsync())
            .Should()
            .BeEmpty();

        // ...but the pool must still see the child's pending question and hard-block a switch on it.
        (await pool.HasPendingAskUserQuestionAsync(threadId))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task ThreadRemoved_FiresOnce_OnRemoveAgentAsync()
    {
        await using var pool = CreatePool();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-removed", mode);

        var notifications = new List<string>();
        pool.ThreadRemoved += id => notifications.Add(id);

        await pool.RemoveAgentAsync("thread-removed");

        notifications.Should().ContainSingle().Which.Should().Be("thread-removed");
    }

    [Fact]
    public async Task ThreadRemoved_DoesNotFire_WhenThreadAlreadyAbsent()
    {
        await using var pool = CreatePool();

        var notifications = new List<string>();
        pool.ThreadRemoved += id => notifications.Add(id);

        // No-op: nothing to dispose, nothing to notify. Listeners (registry) would otherwise see
        // ghost unregister events for threadIds that never existed.
        await pool.RemoveAgentAsync("never-created");

        notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task ThreadRemoved_DoesNotFire_OnRecreateAgentWithModeAsync()
    {
        // F3 regression: mode-switch preserves threadId, so the registry's session→thread map
        // must stay intact across the swap. If ThreadRemoved fired here, the context-discovery
        // injector would lose its route to the freshly-recreated agent.
        await using var pool = CreatePool();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-mode-swap", mode);

        var notifications = new List<string>();
        pool.ThreadRemoved += id => notifications.Add(id);

        var newMode = SystemChatModes.All[0];
        _ = await pool.RecreateAgentWithModeAsync("thread-mode-swap", newMode);

        notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task RecreateAgentWithProviderAsync_OverwritesPersistedProvider_AndPreservesModeAndWorkspace()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai", "anthropic"]);
        var store = new InMemoryConversationStore();
        var created = new List<(string Provider, string? Workspace, string Mode)>();

        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                created.Add((context.ProviderId, context.WorkspaceId, context.Mode.Id));
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(
            "thread-prov-swap",
            mode,
            requestedProviderId: "test",
            requestResponseDumpFileName: null,
            requestedWorkspaceId: "ws-1"
        );
        (await WaitForPersistedProviderAsync(store, "thread-prov-swap")).Should().Be("test");

        _ = await pool.RecreateAgentWithProviderAsync("thread-prov-swap", "openai", mode);

        // The recreated agent used the NEW provider and preserved the thread's mode + workspace.
        created.Should().HaveCount(2);
        created[1].Provider.Should().Be("openai");
        created[1].Workspace.Should().Be("ws-1");
        created[1].Mode.Should().Be(mode.Id);

        // The switch is persisted (overwrite) so a later refresh restores it.
        (await WaitForPersistedProviderAsync(store, "thread-prov-swap"))
            .Should()
            .Be("openai");
        pool.GetEffectiveProviderId("thread-prov-swap", null).Should().Be("openai");
    }

    [Fact]
    public async Task RecreateAgentWithProviderAsync_Throws_AndLeavesThreadUntouched_WhenProviderUnavailable()
    {
        // "openai" is NOT available — the validation must happen BEFORE teardown so the working agent
        // and its persisted provider are left intact (the controller maps this to a clean 503).
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var store = new InMemoryConversationStore();

        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var original = pool.GetOrCreateAgent(
            "thread-prov-bad",
            mode,
            requestedProviderId: "test",
            requestResponseDumpFileName: null
        );
        (await WaitForPersistedProviderAsync(store, "thread-prov-bad")).Should().Be("test");

        var act = async () => await pool.RecreateAgentWithProviderAsync("thread-prov-bad", "openai", mode);
        await act.Should().ThrowAsync<ProviderUnavailableException>();

        // Untouched: same agent instance still pooled, persisted provider still "test".
        ReferenceEquals(pool.GetOrCreateAgent("thread-prov-bad", mode), original).Should().BeTrue();
        pool.GetEffectiveProviderId("thread-prov-bad", null).Should().Be("test");
    }

    [Fact]
    public async Task ThreadRemoved_DoesNotFire_OnRecreateAgentWithProviderAsync()
    {
        // Provider-switch preserves threadId (same as mode-switch), so the session→thread map must
        // stay intact — ThreadRemoved must not fire.
        await using var pool = CreatePool();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-prov-noremove", mode);

        var notifications = new List<string>();
        pool.ThreadRemoved += id => notifications.Add(id);

        _ = await pool.RecreateAgentWithProviderAsync("thread-prov-noremove", "test", mode);

        notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task RecreateAgentWithProviderAsync_SucceedsAndPersists_WhenOldAgentDisposeThrows()
    {
        // Tearing down the PREVIOUS agent can fail (e.g. its provider's CLI is missing / StopAsync
        // throws). The new agent is already swapped in, so the switch must still succeed and persist —
        // not leak the dispose exception as a 500.
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai"]);
        var store = new InMemoryConversationStore();

        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var old = (FakeMultiTurnAgent)
            pool.GetOrCreateAgent(
                "thread-dispose-throw",
                mode,
                requestedProviderId: "test",
                requestResponseDumpFileName: null
            );
        old.ThrowOnDispose = true; // tearing down the old agent will throw

        var newAgent = await pool.RecreateAgentWithProviderAsync("thread-dispose-throw", "openai", mode);

        newAgent.Should().NotBeSameAs(old);
        (await WaitForPersistedProviderAsync(store, "thread-dispose-throw")).Should().Be("openai");
        pool.GetEffectiveProviderId("thread-dispose-throw", null).Should().Be("openai");
    }

    [Fact]
    public async Task RecreateAgentWithProviderAsync_LeavesExistingAgentPooled_WhenNewAgentConstructionThrows()
    {
        // The target provider is available (validation passes), but BUILDING the replacement agent can
        // still throw AFTER validation — e.g. a Workspace-Agent sandbox session or provider CLI fails to
        // start. The switch must be transactional: a construction failure must leave the existing working
        // agent pooled (and the persisted provider untouched), not evict it and strand the conversation
        // with no agent. Surfaces upstream as a clean 503 for a switch that never happened.
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai"]);
        var store = new InMemoryConversationStore();
        var creations = 0;

        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                creations++;
                if (creations >= 2)
                {
                    // Second creation = the recreate. Simulate replacement construction failing.
                    throw new InvalidOperationException("sandbox session failed to start");
                }
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var original = pool.GetOrCreateAgent(
            "thread-prov-construct-throws",
            mode,
            requestedProviderId: "test",
            requestResponseDumpFileName: null
        );
        (await WaitForPersistedProviderAsync(store, "thread-prov-construct-throws")).Should().Be("test");

        var act = async () => await pool.RecreateAgentWithProviderAsync("thread-prov-construct-throws", "openai", mode);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Untouched: the SAME original agent is still pooled (a re-fetch does NOT hit the factory again —
        // proving it was never evicted), and the persisted provider is still the original "test".
        ReferenceEquals(pool.GetOrCreateAgent("thread-prov-construct-throws", mode), original).Should().BeTrue();
        creations
            .Should()
            .Be(2, "the failed recreate is the only extra factory call; the re-fetch reused the pooled agent");
        pool.GetEffectiveProviderId("thread-prov-construct-throws", null).Should().Be("test");
    }

    [Fact]
    public async Task ThreadRemoved_SubscriberException_DoesNotPoisonOtherSubscribers()
    {
        // Defensive: a buggy subscriber must not strand subsequent listeners. The pool wraps
        // the invocation in a try/catch and logs; verifying we don't leak the exception means
        // RemoveAgentAsync completes cleanly even if one subscriber throws.
        await using var pool = CreatePool();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-bad-sub", mode);

        pool.ThreadRemoved += _ => throw new InvalidOperationException("boom");

        var act = async () => await pool.RemoveAgentAsync("thread-bad-sub");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetEffectiveProviderId_ReturnsDefault_WhenNoPersistedProvider()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var store = new InMemoryConversationStore();

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        pool.GetEffectiveProviderId("thread-fresh", null).Should().Be("test");
    }

    [Fact]
    public async Task GetOrCreateAgent_PersistsRequestedWorkspace_OnFirstCreation()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var store = new InMemoryConversationStore();
        var workspaceSeen = new List<string?>();

        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                workspaceSeen.Add(context.WorkspaceId);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(
            "thread-ws",
            mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: "ws-123"
        );

        workspaceSeen.Should().ContainSingle().Which.Should().Be("ws-123");

        var persisted = await WaitForPersistedWorkspaceAsync(store, "thread-ws");
        persisted.Should().Be("ws-123");
    }

    [Fact]
    public async Task GetOrCreateAgent_PersistedWorkspaceWins_OverRequested()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            "thread-ws-locked",
            new ThreadMetadata
            {
                ThreadId = "thread-ws-locked",
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                    MultiTurnAgentPool.WorkspacePropertyKey,
                    "ws-persisted"
                ),
            }
        );

        var workspaceSeen = new List<string?>();
        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                workspaceSeen.Add(context.WorkspaceId);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(
            "thread-ws-locked",
            mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: "ws-requested"
        );

        workspaceSeen.Should().ContainSingle().Which.Should().Be("ws-persisted");
    }

    [Fact]
    public async Task GetOrCreateAgent_LegacyShim_DefaultsWorkspaceToDefault()
    {
        var workspaceSeen = new List<string?>();
        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _, _) =>
            {
                // The four-arg back-compat factory shim does not receive a workspace id; verify the
                // context built by the pool defaults to "default" by reading it back from a context-
                // aware sibling pool below. Here we just confirm the shim still works.
                workspaceSeen.Add(null);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId));
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-shim", mode);

        workspaceSeen.Should().ContainSingle();
    }

    [Fact]
    public async Task GetOrCreateAgent_ContextWorkspaceDefaultsToDefault_WhenNoneRequested()
    {
        var workspaceSeen = new List<string?>();
        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                workspaceSeen.Add(context.WorkspaceId);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-ws-default", mode);

        workspaceSeen.Should().ContainSingle().Which.Should().Be("default");
    }

    /// <summary>
    /// ADR 0009 gives the collaboration bundle a single owner: the first live root builds it and every
    /// descendant receives that same reference. In the sample the root is built inside the pool's agent
    /// factory, so "built once" is not an invariant the collaboration code enforces for itself — it is
    /// entirely inherited from the pool creating at most one agent per thread.
    /// </summary>
    /// <remarks>
    /// A second bundle for the same conversation would not fail loudly. It would come with its own
    /// directory, its own ledger and its own capacity limiter, so agents on either side would simply
    /// not be able to see or address each other, and the total-agent cap would silently double. This
    /// test therefore races the creation path directly rather than trusting that the lock is there:
    /// the callers rendezvous on a <see cref="Barrier"/> so they arrive together, and the assertion is
    /// that the factory ran once and every caller left holding the same agent.
    /// </remarks>
    [Fact]
    public async Task GetOrCreateAgent_ConcurrentCallersForOneThread_ShareASingleCollaborationBundle()
    {
        const int Callers = 32;
        const string ThreadId = "thread-collaboration-race";
        var bundles = new ConcurrentBag<AgentCollaborationSetup>();
        var factoryInvocations = 0;

        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                _ = Interlocked.Increment(ref factoryInvocations);
                // Mirrors Program.cs: the root's bundle is constructed inside the factory, keyed by
                // the conversation, so one factory call means one bundle.
                bundles.Add(
                    AgentCollaborationSetup.CreateRoot(
                        new AgentCollaborationOptions(),
                        collaborationId: context.ThreadId,
                        agentId: context.ThreadId,
                        name: "conversation"
                    )
                );
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        using var barrier = new Barrier(Callers);
        var agents = await Task.WhenAll(
            Enumerable
                .Range(0, Callers)
                .Select(_ =>
                    Task.Factory.StartNew(
                        () =>
                        {
                            barrier.SignalAndWait();
                            return pool.GetOrCreateAgent(ThreadId, mode);
                        },
                        CancellationToken.None,
                        // Dedicated threads: a barrier whose participants queue behind each other on a
                        // bounded thread pool would deadlock instead of contending.
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default
                    )
                )
        );

        factoryInvocations.Should().Be(1, "the per-thread creation lock admits exactly one builder");
        bundles.Should().ContainSingle("a conversation has one collaboration, not one per caller");
        agents.Distinct().Should().ContainSingle("every caller must receive the same pooled root");
        pool.ActiveAgentCount.Should().Be(1);
    }

    // No timeout of its own: the workspace write is the same fire-and-forget metadata write as every
    // other property, so it inherits WaitForPersistedPropertyAsync's budget. Restating one here is how
    // this call site kept its 1s while the shared default was raised to 5s for #343 starvation.
    private static async Task<string> WaitForPersistedWorkspaceAsync(IConversationStore store, string threadId)
    {
        return await WaitForPersistedPropertyAsync(store, threadId, MultiTurnAgentPool.WorkspacePropertyKey);
    }

    private static async Task<string> WaitForPersistedPropertyAsync(
        IConversationStore store,
        string threadId,
        string propertyKey,
        int timeoutMs = 5000
    )
    {
        string? persisted = null;
        await Wait.UntilAsync(
            async () =>
            {
                var metadata = await store.LoadMetadataAsync(threadId);
                persisted =
                    metadata?.Properties != null
                    && metadata.Properties.TryGetValue(propertyKey, out var raw)
                    && raw is string s
                    && !string.IsNullOrWhiteSpace(s)
                        ? s
                        : null;
                return persisted is not null;
            },
            $"the fire-and-forget metadata write persisted '{propertyKey}' for thread '{threadId}'",
            TimeSpan.FromMilliseconds(timeoutMs),
            TimeSpan.FromMilliseconds(20)
        );

        return persisted!;
    }

    /// <summary>
    /// Waits for a spawned sub-agent to park on its own <c>AskUserQuestion</c> call. Since the
    /// <c>SubAgentManager</c> fix that keeps a parked AskUserQuestion non-terminal, <c>state.Completion</c>
    /// (what <see cref="SubAgentManager.ObserveCompletionAsync"/> awaits) is deliberately never resolved
    /// while parked — only the answer-triggered run performs the one true final completion. So tests that
    /// need the child parked (not finished) must instead poll the child loop's own deferred-call registry
    /// directly, mirroring the production-side <c>HasPendingAskUserQuestionAsync</c> check, rather than
    /// waiting on a completion that will never come.
    /// </summary>
    private static Task WaitUntilChildAwaitingQuestionAsync(
        SubAgentManager subAgentManager,
        string agentId,
        CancellationToken ct
    )
    {
        return Wait.UntilAsync(
            async () =>
                subAgentManager.TryGetAgent(agentId, out var childAgent)
                && childAgent is MultiTurnAgentLoop childLoop
                && (await childLoop.GetDeferredToolCallsAsync(ct)).Count > 0,
            $"the spawned child '{agentId}' parked on its own AskUserQuestion, i.e. registered a deferred tool call",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(20),
            cancellationToken: ct
        );
    }

    private static MultiTurnAgentPool CreatePool()
    {
        return new MultiTurnAgentPool(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance
        );
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(params IMessage[] messages)
    {
        foreach (var msg in messages)
        {
            yield return msg;
            await Task.Yield();
        }
    }

    // No timeout of its own, for the same reason as WaitForPersistedWorkspaceAsync above: restating
    // the shared budget here is what let the workspace helper keep 1s while WaitForPersistedPropertyAsync
    // was raised to 5s for #343 starvation. All 7 call sites took the default, so there is nothing to
    // preserve — and the next raise now reaches them.
    private static async Task<string> WaitForPersistedProviderAsync(IConversationStore store, string threadId)
    {
        return await WaitForPersistedPropertyAsync(store, threadId, MultiTurnAgentPool.ProviderPropertyKey);
    }
}

/// <summary>
/// Builds a <see cref="ProviderRegistry"/> that reports a controlled availability set.
/// We construct it via env vars so we exercise the same code path as production —
/// availability is determined at construction time and cached.
/// </summary>
internal sealed class FakeProviderRegistry
{
    private readonly string _defaultProviderId;
    private readonly HashSet<string> _available;

    public FakeProviderRegistry(string defaultProviderId, IEnumerable<string> available)
    {
        _defaultProviderId = defaultProviderId;
        _available = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
    }

    public ProviderRegistry ToReal()
    {
        // Snapshot env vars, set them per the requested availability set, build registry,
        // then restore. The registry caches availability at construction.
        var snapshot = new Dictionary<string, string?>
        {
            ["LM_PROVIDER_MODE"] = Environment.GetEnvironmentVariable("LM_PROVIDER_MODE"),
            ["OPENAI_API_KEY"] = Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            ["ANTHROPIC_API_KEY"] = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
            ["CLAUDE_CLI_PATH"] = Environment.GetEnvironmentVariable("CLAUDE_CLI_PATH"),
            ["COPILOT_CLI_PATH"] = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH"),
        };

        try
        {
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", _defaultProviderId);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", _available.Contains("openai") ? "sk-fake" : null);
            Environment.SetEnvironmentVariable(
                "ANTHROPIC_API_KEY",
                _available.Contains("anthropic") ? "sk-fake" : null
            );
            Environment.SetEnvironmentVariable("CLAUDE_CLI_PATH", null);
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", null);

            var probe = new FakeFileSystemProbe(executablesOnPath: BuildCliList());
            return new ProviderRegistry(probe, mockHostIsRunning: () => HasAvailableMockProvider());
        }
        finally
        {
            foreach (var (k, v) in snapshot)
            {
                Environment.SetEnvironmentVariable(k, v);
            }
        }
    }

    private IEnumerable<string> BuildCliList()
    {
        if (_available.Contains("claude") || _available.Contains("claude-mock"))
        {
            yield return "claude";
        }
        if (_available.Contains("copilot") || _available.Contains("copilot-mock"))
        {
            yield return "copilot";
        }
    }

    private bool HasAvailableMockProvider()
    {
        return _available.Any(providerId => providerId.EndsWith("-mock", StringComparison.OrdinalIgnoreCase));
    }
}
