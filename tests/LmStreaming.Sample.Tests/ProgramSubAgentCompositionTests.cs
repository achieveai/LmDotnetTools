using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Discovery;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests;

public sealed class ProgramSubAgentCompositionTests
{
    [Fact]
    public void ApplyCharacteristicsAgentFactory_InheritedModelPreservesTemplateAgentAndRequestProperties()
    {
        var templateAgent = new Mock<IStreamingAgent>().Object;
        var routedAgent = new Mock<IStreamingAgent>().Object;
        var requestProperties = ImmutableDictionary<string, object?>.Empty.Add("effort", "high");
        var legacyFactory = new Func<IStreamingAgent>(() => templateAgent);
        var characteristicsFactory =
            new Func<SubAgentCharacteristics, SubAgentProviderAgent>(_ =>
                new SubAgentProviderAgent(routedAgent, requestProperties));

        var result = global::Program.ApplyCharacteristicsAgentFactory(
            new SubAgentOptions
            {
                Templates = new Dictionary<string, SubAgentTemplate>
                {
                    ["custom"] = Template("custom", legacyFactory),
                },
            },
            characteristicsFactory);

        var provider = result.Templates["custom"].CharacteristicsAgentFactory!(
            new SubAgentCharacteristics(null, ReasoningEffort.High));

        provider.Agent.Should().BeSameAs(templateAgent);
        provider.ExtraProperties.Should().BeSameAs(requestProperties);
    }

    [Fact]
    public void ApplyCharacteristicsAgentFactory_ExplicitModelUsesRoutedAgent()
    {
        var templateAgent = new Mock<IStreamingAgent>().Object;
        var routedAgent = new Mock<IStreamingAgent>().Object;
        var legacyFactory = new Func<IStreamingAgent>(() => templateAgent);
        var characteristicsFactory =
            new Func<SubAgentCharacteristics, SubAgentProviderAgent>(_ =>
                new SubAgentProviderAgent(
                    routedAgent,
                    ImmutableDictionary<string, object?>.Empty));

        var result = global::Program.ApplyCharacteristicsAgentFactory(
            new SubAgentOptions
            {
                Templates = new Dictionary<string, SubAgentTemplate>
                {
                    ["custom"] = Template("custom", legacyFactory),
                },
            },
            characteristicsFactory);

        var provider = result.Templates["custom"].CharacteristicsAgentFactory!(
            new SubAgentCharacteristics("explicit-model", null)
            {
                IsModelExplicitlySelected = true,
            });

        provider.Agent.Should().BeSameAs(routedAgent);
    }

    [Fact]
    public void ApplyCharacteristicsAgentFactory_InheritedSpawnsReceiveFreshTemplateAgents()
    {
        var createdAgents = new List<IStreamingAgent>();
        Func<IStreamingAgent> legacyFactory = () =>
        {
            var agent = new Mock<IStreamingAgent>().Object;
            createdAgents.Add(agent);
            return agent;
        };
        var result = global::Program.ApplyCharacteristicsAgentFactory(
            new SubAgentOptions
            {
                Templates = new Dictionary<string, SubAgentTemplate>
                {
                    ["custom"] = Template("custom", legacyFactory),
                },
            },
            _ => new SubAgentProviderAgent(
                new Mock<IStreamingAgent>().Object,
                ImmutableDictionary<string, object?>.Empty));

        var first = result.Templates["custom"].CharacteristicsAgentFactory!(
            new SubAgentCharacteristics(null, null));
        var second = result.Templates["custom"].CharacteristicsAgentFactory!(
            new SubAgentCharacteristics(null, null));

        first.Agent.Should().NotBeSameAs(second.Agent);
        createdAgents.Should().Equal(first.Agent, second.Agent);
        first.OwnsAgent.Should().BeTrue();
        second.OwnsAgent.Should().BeTrue();
    }

    [Fact]
    public async Task BindConversationSubAgents_PassesCharacteristicsFactoryToSessionBinding()
    {
        await using var registry = CreateRegistry();
        var legacyFactory = new Func<IStreamingAgent>(() => new Mock<IStreamingAgent>().Object);
        var providerAgent = new Mock<IStreamingAgent>().Object;
        var characteristicsFactory =
            new Func<SubAgentCharacteristics, SubAgentProviderAgent>(_ =>
                new SubAgentProviderAgent(
                    providerAgent,
                    ImmutableDictionary<string, object?>.Empty));
        var templates = new Dictionary<string, SubAgentTemplate>();

        var binding = global::Program.BindConversationSubAgents(
            registry,
            "session",
            "conversation",
            templates,
            legacyFactory,
            characteristicsFactory);

        binding.AgentFactory.Should().BeSameAs(legacyFactory);
        binding.CharacteristicsAgentFactory.Should().BeSameAs(characteristicsFactory);
    }

    [Fact]
    public async Task BindConversationSubAgents_RecreationRefreshesFactoriesAndPreservesTemplateSource()
    {
        await using var registry = CreateRegistry();
        var firstProvider = CreateRespondingAgent().Object;
        var latestProvider = CreateRespondingAgent().Object;
        var firstLegacyFactory = new Func<IStreamingAgent>(() => firstProvider);
        var latestLegacyFactory = new Func<IStreamingAgent>(() => latestProvider);
        var firstCharacteristicsFactory =
            new Func<SubAgentCharacteristics, SubAgentProviderAgent>(_ =>
                new SubAgentProviderAgent(
                    firstProvider,
                    ImmutableDictionary<string, object?>.Empty));
        var latestCharacteristicsFactory =
            new Func<SubAgentCharacteristics, SubAgentProviderAgent>(_ =>
                new SubAgentProviderAgent(
                    latestProvider,
                    ImmutableDictionary<string, object?>.Empty));
        var firstSeed = Template("first-seed", firstLegacyFactory);
        var discovered = Template("discovered", firstLegacyFactory) with
        {
            Description = "retained description",
            WhenToUse = "retained guidance",
            CharacteristicsAgentFactory = firstCharacteristicsFactory,
            DefaultOptions = new GenerateReplyOptions { ModelId = "retained-model" },
            Effort = ReasoningEffort.Medium,
            EnabledTools = ["retained-tool"],
            MaxTurnsPerRun = 7,
        };

        var firstBinding = global::Program.BindConversationSubAgents(
            registry,
            "session",
            "conversation",
            new Dictionary<string, SubAgentTemplate> { ["first-seed"] = firstSeed },
            firstLegacyFactory,
            firstCharacteristicsFactory);
        firstBinding.Source.TryRegister("discovered", discovered).Should().BeTrue();

        var recreatedBinding = global::Program.BindConversationSubAgents(
            registry,
            "session",
            "conversation",
            new Dictionary<string, SubAgentTemplate>
            {
                ["discovered"] = Template("replacement", latestLegacyFactory),
                ["latest-seed"] = Template("latest-seed", latestLegacyFactory),
            },
            latestLegacyFactory,
            latestCharacteristicsFactory);

        recreatedBinding.Source.Should().BeSameAs(firstBinding.Source);
        var retained = recreatedBinding.Source.Templates["discovered"];
        retained.Should().BeEquivalentTo(
            discovered,
            options => options
                .Excluding(template => template.AgentFactory)
                .Excluding(template => template.CharacteristicsAgentFactory),
            "recreation must preserve all first-wins content and metadata");
        retained.Name.Should().NotBe("replacement");
        retained.AgentFactory.Should().BeSameAs(firstLegacyFactory);
        retained.AgentFactory().Should().BeSameAs(firstProvider);
        retained.CharacteristicsAgentFactory!(
                new SubAgentCharacteristics(null, ReasoningEffort.High))
            .Agent.Should().BeSameAs(firstProvider);
        retained.CharacteristicsAgentFactory!(
                new SubAgentCharacteristics("spawn-model", ReasoningEffort.High)
                {
                    IsModelExplicitlySelected = true,
                })
            .Agent.Should().BeSameAs(latestProvider);
        await using var manager = new SubAgentManager(
            Mock.Of<IMultiTurnAgent>(),
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            new SubAgentOptions { Templates = recreatedBinding.Source.Templates },
            recreatedBinding.Source);
        _ = await manager.SpawnAsync("discovered", "invoke the retained template");
        var discoveredAfterRefresh = Template("post-refresh", latestLegacyFactory) with
        {
            CharacteristicsAgentFactory = firstCharacteristicsFactory,
        };
        recreatedBinding.Source.TryRegister("post-refresh", discoveredAfterRefresh).Should().BeTrue();
        recreatedBinding.Source.Templates["post-refresh"].AgentFactory().Should().BeSameAs(latestProvider);
        recreatedBinding.Source.Templates["post-refresh"].CharacteristicsAgentFactory!(
                new SubAgentCharacteristics(null, null))
            .Agent.Should().BeSameAs(latestProvider);
        recreatedBinding.Source.Templates.Should().ContainKey("first-seed").And.ContainKey("latest-seed");
        recreatedBinding.AgentFactory.Should().BeSameAs(latestLegacyFactory);
        recreatedBinding.AgentFactory().Should().BeSameAs(latestProvider);
        recreatedBinding.CharacteristicsAgentFactory.Should().BeSameAs(latestCharacteristicsFactory);
        recreatedBinding.CharacteristicsAgentFactory!(
                new SubAgentCharacteristics("latest-model", ReasoningEffort.High))
            .Agent.Should().BeSameAs(latestProvider);
    }

    [Fact]
    public async Task MarkdownThroughRebindAndSpawn_PreservesInheritedAndRoutedWireMetadata()
    {
        await using var registry = CreateRegistry();
        var models = new[]
        {
            new CopilotModelInfo(
                "parent-model",
                "Parent",
                CopilotModelVendor.OpenAI,
                CopilotModelTransport.Responses)
            {
                ReasoningEfforts = ["low", "medium", "high", "xhigh"],
            },
            new CopilotModelInfo(
                "tier-model",
                "Tier",
                CopilotModelVendor.OpenAI,
                CopilotModelTransport.Responses)
            {
                ReasoningEfforts = ["low", "medium", "high", "xhigh"],
            },
            new CopilotModelInfo(
                "explicit-model",
                "Explicit",
                CopilotModelVendor.OpenAI,
                CopilotModelTransport.Responses)
            {
                ReasoningEfforts = ["low", "medium", "high", "xhigh"],
            },
        };
        var catalog = new ProviderRegistry(models, Mock.Of<IFileSystemProbe>());
        var resolver = new SubAgentModelResolver(
            catalog,
            new SubAgentIntelligenceOptions
            {
                Tiers = new Dictionary<int, string[]> { [3] = ["tier-model"] },
            },
            new CapturingLogger<SubAgentModelResolver>());
        var loader = new WorkspaceSubAgentLoader(
            registry,
            new CapturingLogger<WorkspaceSubAgentLoader>(),
            resolver);
        var inheritedOptions = new List<GenerateReplyOptions?>();
        var routedOptions = new Dictionary<string, GenerateReplyOptions?>();
        Func<IStreamingAgent> inheritedAgentFactory = () =>
            CreateRespondingAgent(options => inheritedOptions.Add(options)).Object;
        Func<SubAgentCharacteristics, SubAgentProviderAgent> staleCharacteristicsFactory =
            _ => throw new InvalidOperationException("The pre-recreation route must not execute.");
        var session = new SandboxSession("default", "session", "default", "workspace");
        var inherited = await loader.LoadOneWithCharacteristicsAsync(
            session,
            InlineAgent(
                "inherited",
                """
                ---
                name: inherited
                effort: medium
                ---
                Use the inherited route.
                """),
            inheritedAgentFactory,
            staleCharacteristicsFactory);
        var tiered = await loader.LoadOneWithCharacteristicsAsync(
            session,
            InlineAgent(
                "tiered",
                """
                ---
                name: tiered
                modelintelligence: 3
                effort: high
                ---
                Use the tier-selected route.
                """),
            () => throw new InvalidOperationException("Tier routing must use the characteristics factory."),
            staleCharacteristicsFactory);
        var explicitTemplate = await loader.LoadOneWithCharacteristicsAsync(
            session,
            InlineAgent(
                "explicit",
                """
                ---
                name: explicit
                model: explicit-model
                effort: xhigh
                ---
                Use the author-pinned route.
                """),
            () => throw new InvalidOperationException("Explicit routing must use the characteristics factory."),
            staleCharacteristicsFactory);
        var initialOptions = global::Program.ApplyCharacteristicsAgentFactory(
            new SubAgentOptions
            {
                Templates = new Dictionary<string, SubAgentTemplate>
                {
                    ["inherited"] = inherited!,
                    ["tiered"] = tiered!,
                    ["explicit"] = explicitTemplate!,
                },
            },
            staleCharacteristicsFactory);
        var firstBinding = global::Program.BindConversationSubAgents(
            registry,
            "session",
            "conversation",
            initialOptions.Templates,
            inheritedAgentFactory,
            staleCharacteristicsFactory);
        var latestFactory = new CharacteristicsAgentFactory(
            catalog,
            CreateRespondingAgent().Object,
            model => CreateRespondingAgent(options => routedOptions[model.Id] = options).Object,
            new CapturingLogger<CharacteristicsAgentFactory>(),
            models[0]);
        var rebound = global::Program.BindConversationSubAgents(
            registry,
            "session",
            "conversation",
            initialOptions.Templates,
            inheritedAgentFactory,
            latestFactory.Create);
        rebound.Source.Should().BeSameAs(firstBinding.Source);
        await using var manager = new SubAgentManager(
            Mock.Of<IMultiTurnAgent>(),
            [],
            new Dictionary<string, ToolHandler>(),
            new SubAgentOptions { Templates = rebound.Source.Templates },
            rebound.Source,
            parentModelId: "parent-model");

        _ = await manager.SpawnAsync("inherited", "inherit");
        _ = await manager.SpawnAsync("tiered", "tier");
        _ = await manager.SpawnAsync("explicit", "explicit");

        inheritedOptions.Should().ContainSingle();
        inheritedOptions[0]!.ModelId.Should().Be("parent-model");
        inheritedOptions[0]!.ExtraProperties["Reasoning"]
            .Should().BeOfType<ResponseReasoningOptions>()
            .Which.Effort.Should().Be("medium");
        routedOptions["tier-model"]!.ModelId.Should().Be("tier-model");
        routedOptions["tier-model"]!.ExtraProperties["Reasoning"]
            .Should().BeOfType<ResponseReasoningOptions>()
            .Which.Effort.Should().Be("high");
        routedOptions["explicit-model"]!.ModelId.Should().Be("explicit-model");
        routedOptions["explicit-model"]!.ExtraProperties["Reasoning"]
            .Should().BeOfType<ResponseReasoningOptions>()
            .Which.Effort.Should().Be("xhigh");
        rebound.Source.Templates["tiered"].IsModelExplicitlySelected.Should().BeFalse();
        rebound.Source.Templates["tiered"].IsModelTierResolved.Should().BeTrue();
        rebound.Source.Templates["explicit"].IsModelExplicitlySelected.Should().BeTrue();
    }

    [Fact]
    public void ApplyDefaultSubAgentStore_WiresSharedStore_WhenOptionsHasNoDefault()
    {
        var store = new Mock<IConversationStore>().Object;
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["custom"] = Template("custom", () => new Mock<IStreamingAgent>().Object),
            },
        };

        var result = global::Program.ApplyDefaultSubAgentStore(options, store);

        result.DefaultConversationStoreFactory.Should().NotBeNull();
        result.DefaultConversationStoreFactory!("subagent-a").Should().BeSameAs(store);
        result.DefaultConversationStoreFactory!("subagent-b").Should().BeSameAs(store);
    }

    [Fact]
    public void ApplyDefaultSubAgentStore_PreservesExistingFactory_WhenAlreadySet()
    {
        var templateStore = new Mock<IConversationStore>().Object;
        var fallbackStore = new Mock<IConversationStore>().Object;
        Func<string, IConversationStore> existingFactory = _ => templateStore;
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["custom"] = Template("custom", () => new Mock<IStreamingAgent>().Object),
            },
            DefaultConversationStoreFactory = existingFactory,
        };

        var result = global::Program.ApplyDefaultSubAgentStore(options, fallbackStore);

        result.Should().BeSameAs(options);
        result.DefaultConversationStoreFactory.Should().BeSameAs(existingFactory);
        result.DefaultConversationStoreFactory!("subagent-a").Should().BeSameAs(templateStore);
    }

    [Fact]
    public async Task SpawnedSubAgent_PersistsTranscript_ViaWiredDefaultStore()
    {
        var fakeStore = new InMemoryConversationStore();
        var templates = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["worker"] = Template("worker", () => CreateRespondingAgent().Object),
        };
        var options = global::Program.ApplyDefaultSubAgentStore(
            new SubAgentOptions { Templates = templates },
            fakeStore);
        var source = new MutableSubAgentTemplateSource(templates);
        await using var manager = new SubAgentManager(
            Mock.Of<IMultiTurnAgent>(),
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options,
            source);

        _ = await manager.SpawnAsync("worker", "persist my transcript");

        var threadId = manager.ListAgents().Should().ContainSingle().Subject.ThreadId;
        threadId.Should().StartWith("subagent-");
        var persisted = await fakeStore.LoadMessagesAsync(threadId);
        persisted.Should().NotBeEmpty();
    }

    private static SandboxSessionRegistry CreateRegistry()
    {
        const string baseUrl = "http://localhost:3000";
        var options = new SandboxGatewayOptions { BaseUrl = baseUrl };
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler()));
        return new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler()),
            new AuthOptions(),
            new AuthSharedSecret(new AuthOptions()));
    }

    private static SubAgentTemplate Template(
        string name,
        Func<IStreamingAgent> agentFactory) =>
        new()
        {
            Name = name,
            SystemPrompt = name,
            AgentFactory = agentFactory,
        };

    private static SandboxSessionRegistry.DiscoveredItem InlineAgent(string name, string content) =>
        new("subagent", name, name, $"/marketplaces/test/{name}.md", content);

    private static Mock<IStreamingAgent> CreateRespondingAgent(
        Action<GenerateReplyOptions?>? captureOptions = null)
    {
        var agent = new Mock<IStreamingAgent>();
        agent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                (_, options, _) => captureOptions?.Invoke(options))
            .ReturnsAsync(ToAsyncEnumerable([
                new TextMessage { Text = "done", Role = Role.Assistant },
            ]));
        return agent;
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IReadOnlyList<IMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP is not expected");
    }
}
