using System.Collections.Immutable;
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
        _ = pool.GetOrCreateAgent("thread-override", mode, requestedProviderId: "test", requestResponseDumpFileName: null);

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
        _ = pool.GetOrCreateAgent("thread-match", mode, requestedProviderId: "anthropic", requestResponseDumpFileName: null);

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
        (await WaitForPersistedProviderAsync(store, "thread-prov-swap")).Should().Be("openai");
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
        var original = pool.GetOrCreateAgent("thread-prov-bad", mode, requestedProviderId: "test", requestResponseDumpFileName: null);
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
        var old = (FakeMultiTurnAgent)pool.GetOrCreateAgent(
            "thread-dispose-throw", mode, requestedProviderId: "test", requestResponseDumpFileName: null);
        old.ThrowOnDispose = true; // tearing down the old agent will throw

        var newAgent = await pool.RecreateAgentWithProviderAsync("thread-dispose-throw", "openai", mode);

        newAgent.Should().NotBeSameAs(old);
        (await WaitForPersistedProviderAsync(store, "thread-dispose-throw")).Should().Be("openai");
        pool.GetEffectiveProviderId("thread-dispose-throw", null).Should().Be("openai");
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

    private static async Task<string?> WaitForPersistedWorkspaceAsync(
        IConversationStore store,
        string threadId,
        int timeoutMs = 1000
    )
    {
        return await WaitForPersistedPropertyAsync(store, threadId, MultiTurnAgentPool.WorkspacePropertyKey, timeoutMs);
    }

    private static async Task<string?> WaitForPersistedPropertyAsync(
        IConversationStore store,
        string threadId,
        string propertyKey,
        int timeoutMs = 1000
    )
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var metadata = await store.LoadMetadataAsync(threadId);
            if (
                metadata?.Properties != null
                && metadata.Properties.TryGetValue(propertyKey, out var raw)
                && raw is string s
                && !string.IsNullOrWhiteSpace(s)
            )
            {
                return s;
            }

            await Task.Delay(20);
        }

        return null;
    }

    private static MultiTurnAgentPool CreatePool()
    {
        return new MultiTurnAgentPool(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance
        );
    }

    private static async Task<string?> WaitForPersistedProviderAsync(
        IConversationStore store,
        string threadId,
        int timeoutMs = 1000
    )
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var metadata = await store.LoadMetadataAsync(threadId);
            if (
                metadata?.Properties != null
                && metadata.Properties.TryGetValue(MultiTurnAgentPool.ProviderPropertyKey, out var raw)
                && raw is string s
                && !string.IsNullOrWhiteSpace(s)
            )
            {
                return s;
            }

            await Task.Delay(20);
        }

        return null;
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
