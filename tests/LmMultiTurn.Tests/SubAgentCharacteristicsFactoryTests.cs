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
    public async Task SpawnAsync_InheritedEffortAppliedOnParentModelReuse()
    {
        // Parent-model reuse with no per-template effort and no model choice: the sub-agent inherits the
        // parent's reasoning floor (Option A — fixed High) so it thinks like the launching conversation.
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
        await using var manager = CreateManager(
            template,
            parentModelId: "shared-model",
            inheritedEffort: ReasoningEffort.High
        );

        _ = await manager.SpawnAsync("test-agent", "test task");

        receivedCharacteristics!.Effort.Should().Be(ReasoningEffort.High);
        receivedCharacteristics.IsModelExplicitlySelected.Should().BeFalse();
    }

    [Fact]
    public async Task SpawnAsync_TemplateEffortOverridesInheritedEffort()
    {
        // "less thinking" wins: a template that lowered its own Effort keeps that value over the inherited floor.
        SubAgentCharacteristics? receivedCharacteristics = null;
        var providerAgent = CreateRespondingAgent();
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => throw new InvalidOperationException("Legacy factory should not run."),
            Effort = ReasoningEffort.Low,
            CharacteristicsAgentFactory = characteristics =>
            {
                receivedCharacteristics = characteristics;
                return new SubAgentProviderAgent(providerAgent.Object, ImmutableDictionary<string, object?>.Empty);
            },
        };
        await using var manager = CreateManager(
            template,
            parentModelId: "shared-model",
            inheritedEffort: ReasoningEffort.High
        );

        _ = await manager.SpawnAsync("test-agent", "test task");

        receivedCharacteristics!.Effort.Should().Be(ReasoningEffort.Low);
    }

    [Fact]
    public async Task SpawnAsync_ExplicitModelOverrideSuppressesInheritedEffort()
    {
        // "different model" wins: an explicit model choice leaves reasoning un-nudged (null effort).
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
        await using var manager = CreateManager(
            template,
            parentModelId: "shared-model",
            inheritedEffort: ReasoningEffort.High
        );

        _ = await manager.SpawnAsync("test-agent", "test task", model: "spawn-model");

        receivedCharacteristics!.Effort.Should().BeNull();
        receivedCharacteristics.IsModelExplicitlySelected.Should().BeTrue();
    }

    [Fact]
    public async Task SpawnAsync_PlainPathSeedsInheritedReasoningWhenNoModelOverride()
    {
        // Plain-path delegate (no characteristics factory) inherits the parent's PRE-SHAPED reasoning.
        GenerateReplyOptions? receivedOptions = null;
        var providerAgent = CreateRespondingAgent(options => receivedOptions = options);
        var inheritedReasoning = ImmutableDictionary<string, object?>.Empty.Add("Thinking", "budget");
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => providerAgent.Object,
        };
        await using var manager = CreateManager(template, inheritedReasoning: inheritedReasoning);

        _ = await manager.SpawnAsync("test-agent", "test task");

        receivedOptions.Should().NotBeNull();
        receivedOptions!.ExtraProperties.Should().Contain("Thinking", "budget");
    }

    [Fact]
    public async Task SpawnAsync_PlainPathDoesNotSeedInheritedReasoningWhenModelOverridden()
    {
        // A different model may use a different transport than the shaped metadata targets, so an explicit
        // model override skips the inherited pre-shaped reasoning.
        GenerateReplyOptions? receivedOptions = null;
        var providerAgent = CreateRespondingAgent(options => receivedOptions = options);
        var inheritedReasoning = ImmutableDictionary<string, object?>.Empty.Add("Thinking", "budget");
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => providerAgent.Object,
        };
        await using var manager = CreateManager(template, inheritedReasoning: inheritedReasoning);

        _ = await manager.SpawnAsync("test-agent", "test task", model: "spawn-model");

        receivedOptions.Should().NotBeNull();
        receivedOptions!.ExtraProperties.Should().NotContainKey("Thinking");
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
    public void SubAgentProviderAgent_RejectsNullAgent()
    {
        var act = () =>
            new SubAgentProviderAgent(null!, ImmutableDictionary<string, object?>.Empty);

        act.Should().Throw<ArgumentNullException>().WithParameterName("Agent");
    }

    [Fact]
    public void SubAgentProviderAgent_RejectsNullExtraProperties()
    {
        var act = () =>
            new SubAgentProviderAgent(
                Agent: Mock.Of<IStreamingAgent>(),
                ExtraProperties: null!
            );

        act.Should().Throw<ArgumentNullException>().WithParameterName("ExtraProperties");
    }

    [Fact]
    public void SubAgentProviderAgent_RejectsNullAgentFromWithExpression()
    {
        var provider = new SubAgentProviderAgent(
            Mock.Of<IStreamingAgent>(),
            ImmutableDictionary<string, object?>.Empty
        );

        var act = () => provider with { Agent = null! };

        act.Should().Throw<ArgumentNullException>().WithParameterName("value");
    }

    [Fact]
    public void SubAgentProviderAgent_RejectsNullExtraPropertiesFromWithExpression()
    {
        var provider = new SubAgentProviderAgent(
            Mock.Of<IStreamingAgent>(),
            ImmutableDictionary<string, object?>.Empty
        );

        var act = () => provider with { ExtraProperties = null! };

        act.Should().Throw<ArgumentNullException>().WithParameterName("value");
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

    private SubAgentManager CreateManager(
        SubAgentTemplate template,
        string? parentModelId = null,
        ReasoningEffort? inheritedEffort = null,
        ImmutableDictionary<string, object?>? inheritedReasoning = null
    )
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
            InheritedEffort = inheritedEffort,
            InheritedReasoning = inheritedReasoning,
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
