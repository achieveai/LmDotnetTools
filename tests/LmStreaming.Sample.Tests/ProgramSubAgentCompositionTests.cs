using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Tests;

public sealed class ProgramSubAgentCompositionTests
{
    [Fact]
    public void ApplyCharacteristicsAgentFactory_AttachesSameFactoryToEveryTemplate()
    {
        var legacyFactory = new Func<IStreamingAgent>(() => new Mock<IStreamingAgent>().Object);
        var characteristicsFactory =
            new Func<SubAgentCharacteristics, SubAgentProviderAgent>(_ =>
                new SubAgentProviderAgent(
                    new Mock<IStreamingAgent>().Object,
                    ImmutableDictionary<string, object?>.Empty));
        var first = Template("first", legacyFactory);
        var second = Template("second", legacyFactory);

        var result = global::Program.ApplyCharacteristicsAgentFactory(
            new SubAgentOptions
            {
                Templates = new Dictionary<string, SubAgentTemplate>
                {
                    ["first"] = first,
                    ["second"] = second,
                },
            },
            characteristicsFactory);

        result.Templates.Values.Should().OnlyContain(template =>
            ReferenceEquals(template.CharacteristicsAgentFactory, characteristicsFactory));
        result.Templates["first"].AgentFactory.Should().BeSameAs(legacyFactory);
        result.Templates["second"].AgentFactory.Should().BeSameAs(legacyFactory);
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
        retained.AgentFactory.Should().BeSameAs(latestLegacyFactory);
        retained.AgentFactory().Should().BeSameAs(latestProvider);
        retained.CharacteristicsAgentFactory.Should().BeSameAs(latestCharacteristicsFactory);
        retained.CharacteristicsAgentFactory!(
                new SubAgentCharacteristics("spawn-model", ReasoningEffort.High))
            .Agent.Should().BeSameAs(latestProvider);
        await using var manager = new SubAgentManager(
            Mock.Of<IMultiTurnAgent>(),
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            new SubAgentOptions { Templates = recreatedBinding.Source.Templates },
            recreatedBinding.Source);
        _ = await manager.SpawnAsync("discovered", "invoke the retained template");
        var discoveredAfterRefresh = Template("post-refresh", firstLegacyFactory) with
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

    private static Mock<IStreamingAgent> CreateRespondingAgent()
    {
        var agent = new Mock<IStreamingAgent>();
        agent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions?>(),
                It.IsAny<CancellationToken>()))
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
