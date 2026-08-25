using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// The <c>Agent</c> tool exposes <c>model</c> as an unconstrained string, so a parent/controller LLM
/// can fill it with an invented id, a value that belongs in another field (a subagent_type, a tier),
/// or a typo. Passed straight through, such a value becomes the request model and hard-fails at the
/// provider with a BadRequest — a wasted spawn plus its tokens. The host-supplied
/// <see cref="SubAgentOptions.ModelOverrideValidator"/> lets the manager DROP an unknown override and
/// fall back to the tier/parent model instead of hard-failing. With no validator the override passes
/// through unchanged (previous behavior).
///
/// A validated override that survives must ALSO be built by the host's transport-aware
/// <see cref="SubAgentOptions.TierAgentFactory"/> for the override id — the plain-path delegate would
/// otherwise reuse the parent/template provider, whose transport may not match the override's model
/// (e.g. an Anthropic-transport override under an OpenAI-Responses parent) and POST to the wrong
/// endpoint. That is the same transport-correct routing the tier path already receives.
/// </summary>
public class SubAgentModelOverrideValidationTests : IAsyncLifetime
{
    private readonly List<SubAgentManager> _managers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Bounded AND best-effort. Bounding each teardown (#362) turned a stall into a throw, and a
        // throw mid-loop would exit DisposeAsync with every LATER manager still undisposed — trading
        // one leak shape for another. Collect, dispose them all, then report together.
        List<Exception>? failures = null;
        foreach (var manager in _managers)
        {
            try
            {
                await Wait.ForTeardownAsync(manager, "a sub-agent manager created by this test");
            }
            catch (Exception ex)
            {
                // Collected, never swallowed: rethrown as an aggregate below.
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "One or more sub-agent managers failed to tear down within their ceiling; every "
                    + "manager was still disposed before this was reported.",
                failures
            );
        }
    }

    [Fact]
    public async Task SpawnAsync_InvalidModelOverride_FallsBackToParentModel()
    {
        // Validator recognizes only "good-model"; an unknown override must be dropped and the
        // sub-agent must fall back to the parent's model rather than sending the bogus id.
        var (manager, capture) = CreateManager(
            parentModelId: "parent-model",
            modelOverrideValidator: model => model == "good-model");

        _ = await manager.SpawnAsync("worker", "do work", model: "bogus-model", name: "a", runInBackground: false);

        capture.ModelId.Should().Be("parent-model",
            "an override the host cannot resolve is ignored and the sub-agent inherits the parent model");
        capture.ModelId.Should().NotBe("bogus-model", "the invalid override must never reach the provider");
    }

    [Fact]
    public async Task SpawnAsync_ValidModelOverride_IsUsed()
    {
        // A recognized override is honored exactly as before.
        var (manager, capture) = CreateManager(
            parentModelId: "parent-model",
            modelOverrideValidator: model => model == "good-model");

        _ = await manager.SpawnAsync("worker", "do work", model: "good-model", name: "b", runInBackground: false);

        capture.ModelId.Should().Be("good-model", "a valid override still wins over the parent model");
    }

    [Fact]
    public async Task SpawnAsync_NoValidator_PassesOverrideThrough()
    {
        // Default behavior (no host validator) is preserved: the override passes through unchanged.
        var (manager, capture) = CreateManager(
            parentModelId: "parent-model",
            modelOverrideValidator: null);

        _ = await manager.SpawnAsync("worker", "do work", model: "bogus-model", name: "c", runInBackground: false);

        capture.ModelId.Should().Be("bogus-model",
            "without a validator the manager keeps its previous pass-through behavior");
    }

    [Fact]
    public async Task SpawnAsync_ValidModelOverride_BuildsProviderViaTierAgentFactory_NotParentTemplate()
    {
        // A validated `model` override may target a DIFFERENT transport than the parent (e.g. an
        // Anthropic model while the parent runs an OpenAI-Responses model). The plain-path delegate must
        // build that override through the host's transport-aware TierAgentFactory for the override id —
        // reusing the parent/template provider would POST the request to the parent's (wrong) transport
        // endpoint and hard-fail with a provider BadRequest (the claude-sonnet-4.6-on-Responses storm).
        var (manager, capture) = CreateManager(
            parentModelId: "parent-model",
            modelOverrideValidator: model => model == "good-model",
            wireTierAgentFactory: true);

        _ = await manager.SpawnAsync("worker", "do work", model: "good-model", name: "d", runInBackground: false);

        capture.TierFactoryModelIds.Should().ContainSingle().Which.Should().Be("good-model",
            "the validated override is built through the transport-correct tier agent factory");
        capture.TemplateFactoryUsed.Should().BeFalse(
            "the parent/template provider (wrong transport for a cross-transport override) must not be used");
        capture.ModelId.Should().Be("good-model", "the override still reaches the provider as the request model");
    }

    [Fact]
    public async Task SpawnAsync_InvalidModelOverride_WithTierFactory_FallsBackToParentTemplate()
    {
        // A dropped (unknown) override must NOT be built through the tier factory — it falls back to the
        // parent/template provider on the parent model, exactly as if no override had been given.
        var (manager, capture) = CreateManager(
            parentModelId: "parent-model",
            modelOverrideValidator: model => model == "good-model",
            wireTierAgentFactory: true);

        _ = await manager.SpawnAsync("worker", "do work", model: "bogus-model", name: "e", runInBackground: false);

        capture.ModelId.Should().Be("parent-model");
        capture.TierFactoryModelIds.Should().BeEmpty("a dropped override must not be built through the tier factory");
        capture.TemplateFactoryUsed.Should().BeTrue("it falls back to the parent/template provider");
    }

    private sealed class ModelCapture
    {
        public string? ModelId { get; set; }

        // Model ids the host's transport-aware TierAgentFactory was asked to build (one per invocation).
        public List<string> TierFactoryModelIds { get; } = [];

        // True once the parent/template AgentFactory was invoked to build the delegate's provider.
        public bool TemplateFactoryUsed { get; set; }
    }

    private (SubAgentManager Manager, ModelCapture Capture) CreateManager(
        string? parentModelId,
        Func<string, bool>? modelOverrideValidator,
        bool wireTierAgentFactory = false)
    {
        var capture = new ModelCapture();

        // Every provider (template- or tier-built) records the request model id, so a test can assert
        // both WHICH factory built the provider and WHAT model reached the provider.
        IStreamingAgent BuildRecordingProvider()
        {
            var provider = new Mock<IStreamingAgent>();
            provider
                .Setup(a => a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()))
                .Returns((IEnumerable<IMessage> _, GenerateReplyOptions? options, CancellationToken _) =>
                {
                    if (!string.IsNullOrWhiteSpace(options?.ModelId))
                    {
                        capture.ModelId = options.ModelId;
                    }
                    return Task.FromResult(SingleMessage(new TextMessage { Text = "done", Role = Role.Assistant }));
                });
            return provider.Object;
        }

        var parentMock = new Mock<IMultiTurnAgent>();
        parentMock.Setup(p => p.ThreadId).Returns("thread-parent");
        parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    SystemPrompt = "You are a test agent.",
                    AgentFactory = () =>
                    {
                        capture.TemplateFactoryUsed = true;
                        return BuildRecordingProvider();
                    },
                },
            },
            MaxConcurrentSubAgents = 5,
            ModelOverrideValidator = modelOverrideValidator,
            TierAgentFactory = wireTierAgentFactory
                ? modelId =>
                {
                    capture.TierFactoryModelIds.Add(modelId);
                    return BuildRecordingProvider();
                }
            : null,
        };

        var manager = new SubAgentManager(
            parentAgent: parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates),
            parentModelId: parentModelId);
        _managers.Add(manager);
        return (manager, capture);
    }

    private static async IAsyncEnumerable<IMessage> SingleMessage(IMessage message)
    {
        yield return message;
        await Task.Yield();
    }
}
