using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace LmMultiTurn.Tests;

public class SubAgentCharacteristicsFactoryTests : LoggingTestBase
{
    public SubAgentCharacteristicsFactoryTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public async Task SpawnAsync_PassesFinalModelOverrideAndTypedEffortToCharacteristicsFactory()
    {
        SubAgentCharacteristics? receivedCharacteristics = null;
        var providerAgent = CreateRespondingAgent();
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => throw new InvalidOperationException("Legacy factory should not run."),
            DefaultOptions = new GenerateReplyOptions { ModelId = "template-model" },
            Effort = ReasoningEffort.Xhigh,
            CharacteristicsAgentFactory = characteristics =>
            {
                receivedCharacteristics = characteristics;
                return new SubAgentProviderAgent(providerAgent.Object, ImmutableDictionary<string, object?>.Empty);
            },
        };
        await using var manager = CreateManager(template, parentModelId: "parent-model");

        Logger.LogDebug(
            "Spawning with model override {ModelOverride} and effort {Effort}",
            "spawn-model",
            ReasoningEffort.Xhigh
        );

        _ = await manager.SpawnAsync("test-agent", "test task", model: "spawn-model");

        receivedCharacteristics
            .Should()
            .Be(new SubAgentCharacteristics("spawn-model", ReasoningEffort.Xhigh) { IsModelExplicitlySelected = true });
    }

    [Fact]
    public async Task SpawnAsync_InheritedParentModelMarksSelectionAsInherited()
    {
        SubAgentCharacteristics? receivedCharacteristics = null;
        var providerAgent = CreateRespondingAgent();
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => throw new InvalidOperationException("Legacy factory should not run."),
            CharacteristicsAgentFactory = characteristics =>
            {
                receivedCharacteristics = characteristics;
                return new SubAgentProviderAgent(providerAgent.Object, ImmutableDictionary<string, object?>.Empty);
            },
        };
        await using var manager = CreateManager(template, parentModelId: "shared-model");

        _ = await manager.SpawnAsync("test-agent", "test task");

        receivedCharacteristics.Should().Be(new SubAgentCharacteristics("shared-model", null));
        receivedCharacteristics!.IsModelExplicitlySelected.Should().BeFalse();
    }

    [Fact]
    public async Task SpawnAsync_ExplicitTemplateModelEqualToParentMarksSelectionAsExplicit()
    {
        SubAgentCharacteristics? receivedCharacteristics = null;
        var providerAgent = CreateRespondingAgent();
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => throw new InvalidOperationException("Legacy factory should not run."),
            DefaultOptions = new GenerateReplyOptions { ModelId = "shared-model" },
            IsModelExplicitlySelected = true,
            CharacteristicsAgentFactory = characteristics =>
            {
                receivedCharacteristics = characteristics;
                return new SubAgentProviderAgent(providerAgent.Object, ImmutableDictionary<string, object?>.Empty);
            },
        };
        await using var manager = CreateManager(template, parentModelId: "shared-model");

        _ = await manager.SpawnAsync("test-agent", "test task");

        receivedCharacteristics
            .Should()
            .Be(new SubAgentCharacteristics("shared-model", null) { IsModelExplicitlySelected = true });
    }

    [Fact]
    public void SubAgentCharacteristics_PreservesTwoValuePositionalApi()
    {
        var characteristics = new SubAgentCharacteristics("model", ReasoningEffort.High)
        {
            IsModelExplicitlySelected = true,
        };

        var (modelId, effort) = characteristics;

        modelId.Should().Be("model");
        effort.Should().Be(ReasoningEffort.High);
    }

    [Fact]
    public async Task SpawnAsync_MergesProviderMetadataWithoutOverwritingTemplateKeys()
    {
        GenerateReplyOptions? receivedOptions = null;
        var providerAgent = CreateRespondingAgent(options => receivedOptions = options);
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => throw new InvalidOperationException("Legacy factory should not run."),
            DefaultOptions = new GenerateReplyOptions
            {
                ExtraProperties = ImmutableDictionary<string, object?>
                    .Empty.Add("template-only", "template-value")
                    .Add("shared", "template-wins"),
            },
            CharacteristicsAgentFactory = _ => new SubAgentProviderAgent(
                providerAgent.Object,
                ImmutableDictionary<string, object?>
                    .Empty.Add("factory-only", "factory-value")
                    .Add("shared", "factory-loses")
            ),
        };
        await using var manager = CreateManager(template);

        _ = await manager.SpawnAsync("test-agent", "test task");

        Logger.LogDebug(
            "Asserting merged provider options with {ExtraPropertyCount} extra properties",
            receivedOptions?.ExtraProperties.Count
        );
        receivedOptions.Should().NotBeNull();
        receivedOptions!.ExtraProperties.Should().Contain("factory-only", "factory-value");
        receivedOptions.ExtraProperties.Should().Contain("template-only", "template-value");
        receivedOptions.ExtraProperties.Should().Contain("shared", "template-wins");
    }

    [Fact]
    public async Task SpawnAsync_NullDefaultOptionsCreatesOptionsForProviderMetadata()
    {
        GenerateReplyOptions? receivedOptions = null;
        var providerAgent = CreateRespondingAgent(options => receivedOptions = options);
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => throw new InvalidOperationException("Legacy factory should not run."),
            DefaultOptions = null,
            CharacteristicsAgentFactory = _ => new SubAgentProviderAgent(
                providerAgent.Object,
                ImmutableDictionary<string, object?>.Empty.Add("factory-only", "factory-value")
            ),
        };
        await using var manager = CreateManager(template);

        _ = await manager.SpawnAsync("test-agent", "test task");

        receivedOptions.Should().NotBeNull();
        receivedOptions!.ExtraProperties.Should().Contain("factory-only", "factory-value");
    }

    [Fact]
    public async Task SpawnAsync_UsesLegacyAgentFactoryWhenCharacteristicsFactoryIsAbsent()
    {
        var legacyFactoryCalls = 0;
        var providerAgent = CreateRespondingAgent();
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () =>
            {
                legacyFactoryCalls++;
                return providerAgent.Object;
            },
        };
        await using var manager = CreateManager(template);

        _ = await manager.SpawnAsync("test-agent", "test task");

        Logger.LogDebug("Asserting legacy factory invocation count {LegacyFactoryCalls}", legacyFactoryCalls);
        legacyFactoryCalls.Should().Be(1);
    }

    private SubAgentManager CreateManager(SubAgentTemplate template, string? parentModelId = null)
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
        };

        return new SubAgentManager(
            Mock.Of<IMultiTurnAgent>(),
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates),
            LoggerFactory.CreateLogger<SubAgentManager>(),
            parentModelId
        );
    }

    private static Mock<IStreamingAgent> CreateRespondingAgent(Action<GenerateReplyOptions?>? captureOptions = null)
    {
        var agent = new Mock<IStreamingAgent>();
        agent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                (_, options, _) => captureOptions?.Invoke(options)
            )
            .ReturnsAsync(ToAsyncEnumerable([new TextMessage { Text = "done", Role = Role.Assistant }]));

        return agent;
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IReadOnlyList<IMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }
}
