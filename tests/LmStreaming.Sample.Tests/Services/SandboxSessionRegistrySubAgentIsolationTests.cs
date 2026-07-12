using System.Collections.Immutable;
using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Regression coverage for the cross-conversation sub-agent bleed: two chat conversations share
/// ONE sandbox session (v1 always uses the <c>"default"</c> workspace), but a sub-agent discovered
/// or registered while conversation A is live must NOT leak into a NEW conversation B that starts
/// later. The Agent tool's <c>subagent_type</c> catalog (the text a model literally sees — what the
/// <c>tools_echo</c> probe echoes) is rendered from each conversation's
/// <see cref="MutableSubAgentTemplateSource"/>, so isolation has to hold at the source level.
/// </summary>
public class SandboxSessionRegistrySubAgentIsolationTests
{
    private const string SessionId = "session-shared";
    private const string GatewayBaseUrl = "http://localhost:3000";

    private static readonly Func<IStreamingAgent> AgentFactory = () => new Mock<IStreamingAgent>().Object;

    /// <summary>
    /// Conversation A registers (as the context-discovery webhook would) a discovered sub-agent into
    /// the shared session. A later conversation B — created against the same session but seeded only
    /// with the static built-ins — must not see A's discovered sub-agent in its Agent-tool catalog.
    /// </summary>
    [Fact]
    public async Task NewConversation_DoesNotInheritEarlierConversationsDiscoveredSubAgent()
    {
        await using var registry = CreateRegistry();

        var staticSeed = new Dictionary<string, SubAgentTemplate>
        {
            ["researcher"] = Template("Researcher", "Researches topics."),
        };

        // Conversation A boots first and wires the static built-ins for the shared session.
        var bindingA = registry.GetOrAddSubAgentBinding(SessionId, "conversation-A", staticSeed, AgentFactory);

        // While A is live the gateway discovers a workspace sub-agent; the webhook registers it into
        // A's catalog (this is ContextDiscoveryController.TryActivateSubAgentAsync's TryRegister step).
        registry.TryGetSubAgentBinding(SessionId, "conversation-A", out var aForWebhook).Should().BeTrue();
        aForWebhook!.Source.TryRegister(
            "alpha_discovered",
            Template("AlphaDiscovered", "Discovered by conversation A.")).Should().BeTrue();

        bindingA.Source.Templates.Keys.Should().Contain("alpha_discovered");

        // A brand-new conversation B starts on the SAME session, seeded only with the static built-ins.
        var bindingB = registry.GetOrAddSubAgentBinding(SessionId, "conversation-B", staticSeed, AgentFactory);

        // B's catalog source must carry the statics but NOT A's discovered sub-agent.
        bindingB.Source.Templates.Keys.Should().Contain("researcher");
        bindingB.Source.Templates.Keys.Should().NotContain(
            "alpha_discovered",
            "a new conversation must not inherit sub-agents discovered/registered by a different conversation");

        // Probe what B's LLM would actually see: the Agent tool description (the catalog the
        // tools_echo probe surfaces) must not mention A's discovered sub-agent.
        var agentDescriptionForB = SubAgentToolDescriptionProbe.Render(bindingB.Source);
        agentDescriptionForB.Should().Contain("researcher");
        agentDescriptionForB.Should().NotContain(
            "alpha_discovered",
            "conversation B's Agent tool catalog (what its model sees) must be isolated from A's discoveries");
    }

    /// <summary>
    /// Mid-session activation must still reach conversations that are ALREADY live: a discovery that
    /// arrives while conversation A is active lands in A's catalog. (Isolation is for conversations
    /// that start LATER, not a blanket suppression of webhook activation.)
    /// </summary>
    [Fact]
    public async Task DiscoveredSubAgent_StillReachesTheLiveConversationThatTriggeredIt()
    {
        await using var registry = CreateRegistry();

        var staticSeed = new Dictionary<string, SubAgentTemplate>
        {
            ["researcher"] = Template("Researcher", "Researches topics."),
        };

        var bindingA = registry.GetOrAddSubAgentBinding(SessionId, "conversation-A", staticSeed, AgentFactory);

        registry.TryGetSubAgentBinding(SessionId, "conversation-A", out var aForWebhook).Should().BeTrue();
        aForWebhook!.Source.TryRegister(
            "alpha_discovered",
            Template("AlphaDiscovered", "Discovered by conversation A.")).Should().BeTrue();

        bindingA.Source.Templates.Keys.Should().Contain("alpha_discovered");
    }

    [Fact]
    public async Task GetOrAddSubAgentBinding_PreservesLegacyFactory()
    {
        await using var registry = CreateRegistry();
        var seed = new Dictionary<string, SubAgentTemplate>();

        var fourParameterOverload = typeof(SandboxSessionRegistry).GetMethod(
            nameof(SandboxSessionRegistry.GetOrAddSubAgentBinding),
            [
                typeof(string),
                typeof(string),
                typeof(IReadOnlyDictionary<string, SubAgentTemplate>),
                typeof(Func<IStreamingAgent>),
            ]);
        var legacyBinding = registry.GetOrAddSubAgentBinding(
            SessionId,
            "legacy-conversation",
            seed,
            AgentFactory);
        var (source, agentFactory) = legacyBinding;

        fourParameterOverload.Should().NotBeNull("existing compiled callers require the original CLR signature");
        source.Should().BeSameAs(legacyBinding.Source);
        agentFactory.Should().BeSameAs(AgentFactory);
        legacyBinding.AgentFactory.Should().BeSameAs(AgentFactory);
        legacyBinding.CharacteristicsAgentFactory.Should().BeNull();
    }

    [Fact]
    public async Task AddOrUpdateSubAgentBinding_RebindsFactoriesAndPreservesSourceIdentity()
    {
        await using var registry = CreateRegistry();
        var firstAgent = new Mock<IStreamingAgent>().Object;
        var latestAgent = new Mock<IStreamingAgent>().Object;
        Func<IStreamingAgent> firstFactory = () => firstAgent;
        Func<IStreamingAgent> latestFactory = () => latestAgent;
        Func<SubAgentCharacteristics, SubAgentProviderAgent> latestCharacteristicsFactory =
            _ => new SubAgentProviderAgent(
                latestAgent,
                ImmutableDictionary<string, object?>.Empty);
        var seed = new Dictionary<string, SubAgentTemplate>
        {
            ["retained"] = new()
            {
                Name = "retained",
                SystemPrompt = "retained",
                AgentFactory = firstFactory,
            },
        };

        var before = registry.AddOrUpdateSubAgentBinding(
            SessionId,
            "conversation",
            seed,
            firstFactory,
            null);
        var after = registry.AddOrUpdateSubAgentBinding(
            SessionId,
            "conversation",
            seed,
            latestFactory,
            latestCharacteristicsFactory);

        ReferenceEquals(before.Source, after.Source).Should().BeTrue();
        after.AgentFactory.Should().BeSameAs(latestFactory);
        after.CharacteristicsAgentFactory.Should().BeSameAs(latestCharacteristicsFactory);
        after.Source.Templates["retained"].CharacteristicsAgentFactory!(
                new SubAgentCharacteristics("explicit", null)
                {
                    IsModelExplicitlySelected = true,
                })
            .Agent.Should().BeSameAs(latestAgent);
    }

    /// <summary>
    /// A conversation's per-conversation sub-agent binding must be released when its thread is torn
    /// down (the pool raises ThreadRemoved → UnregisterThreadFromAllSessions). Per-conversation
    /// bindings are created on every new chat, so without cleanup they accumulate unbounded for the
    /// process lifetime.
    /// </summary>
    [Fact]
    public async Task UnregisterThread_ReleasesThatConversationsSubAgentBinding()
    {
        await using var registry = CreateRegistry();

        var staticSeed = new Dictionary<string, SubAgentTemplate>
        {
            ["researcher"] = Template("Researcher", "Researches topics."),
        };

        _ = registry.GetOrAddSubAgentBinding(SessionId, "conversation-A", staticSeed, AgentFactory);
        registry.TryGetSubAgentBinding(SessionId, "conversation-A", out _).Should().BeTrue();

        registry.UnregisterThreadFromAllSessions("conversation-A");

        registry.TryGetSubAgentBinding(SessionId, "conversation-A", out var binding)
            .Should().BeFalse("a torn-down conversation's binding must be freed, not retained for the process lifetime");
        binding.Should().BeNull();
        registry.GetSubAgentBindingsForSession(SessionId).Should().BeEmpty();
    }

    private static SubAgentTemplate Template(string name, string description) =>
        new()
        {
            Name = name,
            Description = description,
            SystemPrompt = $"You are {name}.",
            AgentFactory = AgentFactory,
        };

    private static SandboxSessionRegistry CreateRegistry()
    {
        static HttpResponseMessage Unused(HttpRequestMessage _) => new(HttpStatusCode.OK);

        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(Unused)));

        return new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(Unused)),
            new AuthOptions(),
            new AuthSharedSecret(new AuthOptions()));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }
}
