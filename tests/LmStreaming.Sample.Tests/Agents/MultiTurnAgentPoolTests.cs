using System.Collections.Immutable;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;

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
            NullLogger<MultiTurnAgentPool>.Instance);

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-x", mode, requestedProviderId: "openai", requestResponseDumpFileName: null);

        providerSeen.Should().ContainSingle().Which.Should().Be("openai");

        // Persistence is fire-and-forget; allow it to complete before asserting.
        var persisted = await WaitForPersistedProviderAsync(store, "thread-x");
        persisted.Should().Be("openai");
    }

    [Fact]
    public async Task GetOrCreateAgent_PersistedProviderWins_OverRequested()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai", "anthropic"]);
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync("thread-y", new ThreadMetadata
        {
            ThreadId = "thread-y",
            LastUpdated = 1,
            Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                MultiTurnAgentPool.ProviderPropertyKey,
                "anthropic"),
        });

        var providerSeen = new List<string>();
        await using var pool = new MultiTurnAgentPool(
            (threadId, _, providerId, _) =>
            {
                providerSeen.Add(providerId);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId));
            },
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance);

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-y", mode, requestedProviderId: "openai", requestResponseDumpFileName: null);

        providerSeen.Should().ContainSingle().Which.Should().Be("anthropic");
    }

    [Fact]
    public async Task GetOrCreateAgent_Throws_WhenPersistedProviderUnavailable()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync("thread-z", new ThreadMetadata
        {
            ThreadId = "thread-z",
            LastUpdated = 1,
            Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                MultiTurnAgentPool.ProviderPropertyKey,
                "openai"),
        });

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance);

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var act = () => pool.GetOrCreateAgent("thread-z", mode, requestedProviderId: null, requestResponseDumpFileName: null);

        act.Should().Throw<ProviderUnavailableException>()
            .Which.ProviderId.Should().Be("openai");
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
            NullLogger<MultiTurnAgentPool>.Instance);

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var act = () => pool.GetOrCreateAgent("thread-q", mode, requestedProviderId: "openai", requestResponseDumpFileName: null);

        act.Should().Throw<ProviderUnavailableException>()
            .Which.ProviderId.Should().Be("openai");
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
            NullLogger<MultiTurnAgentPool>.Instance);

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
            NullLogger<MultiTurnAgentPool>.Instance);

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent("thread-legacy", mode);

        providerSeen.Should().ContainSingle();
    }

    [Fact]
    public async Task GetEffectiveProviderId_ReturnsPersisted_EvenWhenUnavailable()
    {
        var registry = new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]);
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync("thread-eff", new ThreadMetadata
        {
            ThreadId = "thread-eff",
            LastUpdated = 1,
            Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                MultiTurnAgentPool.ProviderPropertyKey,
                "openai"),
        });

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            registry.ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance);

        pool.GetEffectiveProviderId("thread-eff", null).Should().Be("openai");
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
            NullLogger<MultiTurnAgentPool>.Instance);

        pool.GetEffectiveProviderId("thread-fresh", null).Should().Be("test");
    }

    private static MultiTurnAgentPool CreatePool()
    {
        return new MultiTurnAgentPool(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance);
    }

    private static async Task<string?> WaitForPersistedProviderAsync(
        IConversationStore store,
        string threadId,
        int timeoutMs = 1000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var metadata = await store.LoadMetadataAsync(threadId);
            if (metadata?.Properties != null
                && metadata.Properties.TryGetValue(MultiTurnAgentPool.ProviderPropertyKey, out var raw)
                && raw is string s
                && !string.IsNullOrWhiteSpace(s))
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
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", _available.Contains("anthropic") ? "sk-fake" : null);
            Environment.SetEnvironmentVariable("CLAUDE_CLI_PATH", null);
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", null);

            var probe = new FakeFileSystemProbe(
                executablesOnPath: BuildCliList());
            return new ProviderRegistry(probe);
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
        if (_available.Contains("claude"))
        {
            yield return "claude";
        }
        if (_available.Contains("copilot"))
        {
            yield return "copilot";
        }
    }
}
