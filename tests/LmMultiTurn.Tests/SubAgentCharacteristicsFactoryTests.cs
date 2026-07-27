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
    public async Task SpawnAsync_TierResolvedModel_CharacteristicsPath_MarksTierResolvedAndUsesResolvedModel()
    {
        // #3: a per-spawn modelIntelligence tier (the Agent tool's argument, or a workflow task's tier)
        // that the host resolver maps to a concrete model must reach the characteristics factory as a
        // TIER-RESOLVED model on the resolved id — so the factory builds a real provider for it rather
        // than handing back the parent. A tier (like an explicit model) also suppresses the inherited
        // effort floor: the child runs un-nudged on the tier model.
        SubAgentCharacteristics? receivedCharacteristics = null;
        var providerAgent = CreateRespondingAgent();
        var resolverCalls = new List<int>();
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
            parentModelId: "parent-model",
            inheritedEffort: ReasoningEffort.High,
            tierModelResolver: tier =>
            {
                resolverCalls.Add(tier);
                return tier == 5 ? "tier-5-model" : null;
            }
        );

        _ = await manager.SpawnAsync("test-agent", "test task", modelIntelligence: 5);

        resolverCalls.Should().ContainSingle().Which.Should().Be(5);
        receivedCharacteristics!.ModelId.Should().Be("tier-5-model");
        receivedCharacteristics.IsModelTierResolved.Should().BeTrue();
        receivedCharacteristics.IsModelExplicitlySelected.Should().BeFalse();
        receivedCharacteristics.Effort.Should().BeNull("a tier-resolved model runs un-nudged, like an explicit model");
    }

    [Fact]
    public async Task SpawnAsync_TierResolvedModel_PlainPath_BuildsTransportCorrectProviderViaTierAgentFactory()
    {
        // #3 (workflow controller path): a controller delegate takes the PLAIN path (no characteristics
        // factory). A tier that resolves to a model whose transport may differ from the controller's own
        // must be served by a provider built for THAT model via TierAgentFactory — NOT by the plain
        // template.AgentFactory() (which builds the controller's transport). The resolved id also becomes
        // the request ModelId, and the parent's pre-shaped InheritedReasoning is NOT seeded (a different
        // transport would reject it).
        GenerateReplyOptions? tierAgentOptions = null;
        var tierAgent = CreateRespondingAgent(options => tierAgentOptions = options);
        string? tierFactoryRequestedModel = null;
        var inheritedReasoning = ImmutableDictionary<string, object?>.Empty.Add("Thinking", "budget");
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () =>
                throw new InvalidOperationException("Plain template factory must NOT run when a tier resolves."),
        };
        await using var manager = CreateManager(
            template,
            parentModelId: "controller-model",
            inheritedReasoning: inheritedReasoning,
            tierModelResolver: tier => tier == 3 ? "tier-3-model" : null,
            tierAgentFactory: model =>
            {
                tierFactoryRequestedModel = model;
                return tierAgent.Object;
            }
        );

        _ = await manager.SpawnAsync("test-agent", "test task", modelIntelligence: 3);

        tierFactoryRequestedModel.Should().Be("tier-3-model", "the plain path must build the provider for the resolved tier model");
        tierAgentOptions.Should().NotBeNull("the tier-built provider must be the agent that runs");
        tierAgentOptions!.ModelId.Should().Be("tier-3-model");
        tierAgentOptions.ExtraProperties.Should().NotContainKey(
            "Thinking",
            "a tier-resolved model may use a different transport, so the controller's pre-shaped reasoning is not seeded"
        );
    }

    [Fact]
    public async Task SpawnAsync_TierResolvesNull_PlainPath_FallsBackToTemplateFactoryAndSeedsInheritedReasoning()
    {
        // When the resolver returns null (unmapped tier / no routable candidate), the plain path is
        // unchanged: it uses template.AgentFactory() and seeds the inherited pre-shaped reasoning, exactly
        // as a no-tier delegate does. TierAgentFactory must NOT be consulted.
        GenerateReplyOptions? templateAgentOptions = null;
        var templateAgent = CreateRespondingAgent(options => templateAgentOptions = options);
        var inheritedReasoning = ImmutableDictionary<string, object?>.Empty.Add("Thinking", "budget");
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => templateAgent.Object,
        };
        await using var manager = CreateManager(
            template,
            parentModelId: "controller-model",
            inheritedReasoning: inheritedReasoning,
            tierModelResolver: _ => null,
            tierAgentFactory: _ =>
                throw new InvalidOperationException("TierAgentFactory must NOT run when the tier resolves to null.")
        );

        _ = await manager.SpawnAsync("test-agent", "test task", modelIntelligence: 7);

        templateAgentOptions.Should().NotBeNull("an unresolved tier falls back to the template's own provider");
        templateAgentOptions!.ExtraProperties.Should().Contain("Thinking", "budget");
    }

    [Fact]
    public async Task SpawnAsync_ExplicitModelWithTier_PlainPath_SkipsResolverButBuildsTransportCorrectProviderViaTierAgentFactory()
    {
        // An explicit model override wins over a tier, so the tier RESOLVER is never consulted. But the
        // override still needs a provider whose TRANSPORT matches it: on the plain path the override is
        // built via TierAgentFactory for the override id (transport-correct) rather than the plain
        // template.AgentFactory() (which builds the controller's transport — a cross-transport override
        // there POSTs to the wrong endpoint and hard-fails at the provider with a BadRequest). Since a
        // model was chosen, the parent's pre-shaped inherited reasoning is not seeded.
        GenerateReplyOptions? tierAgentOptions = null;
        var tierAgent = CreateRespondingAgent(options => tierAgentOptions = options);
        string? tierFactoryRequestedModel = null;
        var inheritedReasoning = ImmutableDictionary<string, object?>.Empty.Add("Thinking", "budget");
        var resolverCalls = 0;
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () =>
                throw new InvalidOperationException("Plain template factory must NOT run for a cross-transport override."),
        };
        await using var manager = CreateManager(
            template,
            parentModelId: "controller-model",
            inheritedReasoning: inheritedReasoning,
            tierModelResolver: _ =>
            {
                resolverCalls++;
                return "tier-model";
            },
            tierAgentFactory: model =>
            {
                tierFactoryRequestedModel = model;
                return tierAgent.Object;
            }
        );

        _ = await manager.SpawnAsync("test-agent", "test task", model: "spawn-model", modelIntelligence: 3);

        resolverCalls.Should().Be(0, "an explicit model override short-circuits tier resolution");
        tierFactoryRequestedModel.Should().Be("spawn-model", "the override is built transport-correctly for its own id");
        tierAgentOptions.Should().NotBeNull("the tier-built provider is the agent that runs");
        tierAgentOptions!.ModelId.Should().Be("spawn-model");
        tierAgentOptions.ExtraProperties.Should().NotContainKey("Thinking");
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
        ImmutableDictionary<string, object?>? inheritedReasoning = null,
        Func<int, string?>? tierModelResolver = null,
        Func<string, IStreamingAgent>? tierAgentFactory = null
    )
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
            InheritedEffort = inheritedEffort,
            InheritedReasoning = inheritedReasoning,
            TierModelResolver = tierModelResolver,
            TierAgentFactory = tierAgentFactory,
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
