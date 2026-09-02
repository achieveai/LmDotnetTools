using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Unit tests for SubAgentToolProvider: verifies the Agent, SendMessage, and
/// CheckAgent tool descriptors are generated correctly (including the embedded
/// template catalog) and that handler argument validation works.
/// </summary>
public class SubAgentToolProviderTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly Mock<IStreamingAgent> _subAgentMock = new();
    private SubAgentManager? _manager;
    private SubAgentToolProvider? _provider;
    private MutableSubAgentTemplateSource? _source;

    public Task InitializeAsync()
    {
        _parentMock
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        var researcher = new SubAgentTemplate
        {
            SystemPrompt = "You are a researcher.",
            Description = "Researches topics and summarizes findings.",
            WhenToUse = "Use for open-ended investigation across many sources.",
            AgentFactory = () => _subAgentMock.Object,
        };

        var coder = new SubAgentTemplate
        {
            SystemPrompt = "You are a coder.",
            Description = "Writes and edits code.",
            WhenToUse = "Use for focused implementation tasks.",
            AgentFactory = () => _subAgentMock.Object,
        };

        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["researcher"] = researcher, ["coder"] = coder },
            MaxConcurrentSubAgents = 5,
        };

        _source = new MutableSubAgentTemplateSource(options.Templates);

        _manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: _source
        );

        _provider = new SubAgentToolProvider(_manager, _source);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            // Bounded: an unbounded teardown turns one stalled test into an aborted run (#362).
            await Wait.ForTeardownAsync(_manager, "the sub-agent manager under test");
        }
    }

    private static async IAsyncEnumerable<IMessage> BlockingStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    [Fact]
    public void GetFunctions_ReturnsTheLegacyFour()
    {
        // Act
        var functions = _provider!.GetFunctions().ToList();

        // Assert
        functions.Should().HaveCount(4);
        functions
            .Select(f => f.Contract.Name)
            .Should()
            .BeEquivalentTo(["Agent", "SendMessage", "CheckAgent", "WaitAgent"]);
    }

    [Fact]
    public void AgentDescriptor_EmbedsTemplateCatalog()
    {
        // Act
        var agent = _provider!.GetFunctions().First(f => f.Contract.Name == "Agent");

        // Assert: each template's key, Description, and WhenToUse appear in the
        // tool description so the parent LLM can pick the right sub-agent type.
        var description = agent.Contract.Description!;
        description.Should().Contain("researcher");
        description.Should().Contain("Researches topics and summarizes findings.");
        description.Should().Contain("Use for open-ended investigation across many sources.");
        description.Should().Contain("coder");
        description.Should().Contain("Writes and edits code.");
    }

    [Fact]
    public void AgentDescriptor_HasParityParameters()
    {
        // Act
        var agent = _provider!.GetFunctions().First(f => f.Contract.Name == "Agent");
        var paramNames = agent.Contract.Parameters!.Select(p => p.Name).ToList();

        // Assert: Claude Code parity parameters present; legacy ones gone.
        paramNames
            .Should()
            .Contain([
                "subagent_type",
                "prompt",
                "description",
                "name",
                "model",
                "run_in_background",
                "add_tools",
                "remove_tools",
            ]);
        paramNames.Should().NotContain("template_name");
        paramNames.Should().NotContain("task");
        paramNames.Should().NotContain("agent_id");
    }

    [Fact]
    public void AgentDescriptor_SteersTowardReusingLiveSubAgentsViaSendMessage()
    {
        // Act
        var agent = _provider!.GetFunctions().First(f => f.Contract.Name == "Agent");

        // Assert: the Agent description nudges the controller/loop to CONTINUE a still-live
        // sub-agent with SendMessage before spawning a brand-new one for the same/follow-up work.
        var description = agent.Contract.Description!;
        description.Should().Contain("SendMessage");
        description.Should().Contain("before spawning a NEW sub-agent");
    }

    [Fact]
    public void AgentDescriptor_NameParameter_AsksForAReadableHandleAndNotesAutoDerivedFallback()
    {
        // Act
        var agent = _provider!.GetFunctions().First(f => f.Contract.Name == "Agent");
        var nameParam = agent.Contract.Parameters!.First(p => p.Name == "name");

        // Assert: guidance asks for a short human-readable handle and documents that the host
        // auto-derives one when omitted (so no agent ever surfaces as a bare id).
        var desc = nameParam.Description!;
        desc.Should().Contain("human-readable");
        desc.Should().Contain("auto-derived");
    }

    /// <summary>
    /// #641 F-005. This description is the only half of #638 the MODEL reads, on every spawn, and so
    /// the only half with a path to PREVENTING the over-grant rather than reporting it afterwards.
    /// The warning in <c>SubAgentManager</c> fires after the child already holds the tools the caller
    /// meant to withhold. A silent wording regression here would put the whole class back, so the
    /// three load-bearing claims are pinned: exact names only, no wildcard/group language on this
    /// side, and the composition that replaces it.
    /// </summary>
    [Fact]
    public void AgentDescriptor_RemoveToolsParameter_SaysExactNamesOnly_AndNamesTheAddToolsStarComposition()
    {
        // Act
        var agent = _provider!.GetFunctions().First(f => f.Contract.Name == "Agent");
        var removeTools = agent.Contract.Parameters!.First(p => p.Name == "remove_tools");

        // Assert
        var desc = removeTools.Description!;
        desc.Should().Contain("EXACT tool names");
        desc.Should()
            .Contain("NO wildcard or group pattern", "a model that thinks 'tasks:*' works here silently over-grants");
        desc.Should().Contain("'*'").And.Contain("'tasks:*'");
        desc.Should()
            .Contain(
                "add_tools '*'",
                "'everything except a few' is only expressible as add_tools '*' plus exact names"
            );
    }

    [Fact]
    public void SendMessageDescriptor_PrefersContinuationOverSpawningANewAgent()
    {
        // Act
        var sendMessage = _provider!.GetFunctions().First(f => f.Contract.Name == "SendMessage");

        // Assert: SendMessage steers the model to prefer continuing an existing agent over
        // spawning a fresh one when it already has the context for the work.
        var description = sendMessage.Contract.Description!;
        description.Should().Contain("PREFER THIS");
        description.Should().Contain("spawning a new Agent");
    }

    [Fact]
    public async Task HandleAgentToolAsync_MissingPrompt_ThrowsArgumentException()
    {
        // Arrange
        var agentHandler = GetHandler("Agent");
        var args = JsonSerializer.Serialize(new { subagent_type = "researcher" });

        // Act
        var act = () => agentHandler(args, new ToolCallContext(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*prompt*required*");
    }

    [Fact]
    public async Task HandleAgentToolAsync_MissingSubagentType_ThrowsArgumentException()
    {
        // Arrange
        var agentHandler = GetHandler("Agent");
        var args = JsonSerializer.Serialize(new { prompt = "do something" });

        // Act
        var act = () => agentHandler(args, new ToolCallContext(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*subagent_type*required*");
    }

    [Fact]
    public async Task HandleAgentToolAsync_UnknownSubagentType_ReturnsRecoverableError()
    {
        // An unresolvable subagent_type (no exact match, no unique skill-segment match) is a MODEL
        // mistake, not a host fault: the handler must return a recoverable error result listing the
        // available agents — NOT throw — so the loop hands the LLM the catalog to self-correct with
        // instead of the run collapsing to general-purpose.
        var agentHandler = GetHandler("Agent");
        var args = JsonSerializer.Serialize(new { subagent_type = "no-such-agent", prompt = "do something" });

        var result = await agentHandler(args, new ToolCallContext(), CancellationToken.None);

        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.IsError.Should().BeTrue();
        resolved.Payload.ErrorCode.Should().Be("unknown_subagent_type");
        resolved.Payload.Text.Should().Contain("Unknown template").And.Contain("researcher").And.Contain("coder");
    }

    [Theory]
    [InlineData("no-such-agent")]
    [InlineData("shared")]
    public async Task HandleAgentToolAsync_UnresolvableTypeDoesNotInvokeTypePolicy(string requestedType)
    {
        var typePolicyInvocations = 0;
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["plugin-a:shared"] = new SubAgentTemplate
                {
                    SystemPrompt = "a",
                    AgentFactory = () => _subAgentMock.Object,
                },
                ["plugin-b:shared"] = new SubAgentTemplate
                {
                    SystemPrompt = "b",
                    AgentFactory = () => _subAgentMock.Object,
                },
            },
            SpawnTypeModelSelectionResolver = _ =>
            {
                typePolicyInvocations++;
                return new SubAgentSpawnModelSelection(null, 5, "type-policy");
            },
        };
        var source = new MutableSubAgentTemplateSource(options.Templates);
        await using var manager = new SubAgentManager(
            _parentMock.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            source
        );
        var provider = new SubAgentToolProvider(manager, source);
        var handler = provider.GetFunctions().First(f => f.Contract.Name == "Agent").Handler;

        var result = await handler(
            JsonSerializer.Serialize(new { subagent_type = requestedType, prompt = "work" }),
            new ToolCallContext(),
            CancellationToken.None
        );

        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.ErrorCode.Should().Be("unknown_subagent_type");
        typePolicyInvocations.Should().Be(0, "routing policy applies only after a canonical type resolves");
    }

    [Fact]
    public async Task HandleAgentToolAsync_QueueFull_ReturnsRecoverableError()
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["researcher"] = new SubAgentTemplate
                {
                    SystemPrompt = "research",
                    AgentFactory = () => _subAgentMock.Object,
                },
            },
            MaxConcurrentSubAgents = 1,
            MaxQueuedSubAgents = 0,
        };
        var source = new MutableSubAgentTemplateSource(options.Templates);
        await using var manager = new SubAgentManager(
            _parentMock.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            source
        );
        var provider = new SubAgentToolProvider(manager, source);
        var handler = provider.GetFunctions().First(f => f.Contract.Name == "Agent").Handler;
        _subAgentMock
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                (_, _, ct) => Task.FromResult(BlockingStream(ct))
            );
        _ = await manager.SpawnAsync("researcher", "first", runInBackground: true);

        var result = await handler(
            JsonSerializer.Serialize(
                new
                {
                    subagent_type = "researcher",
                    prompt = "overflow",
                    run_in_background = true,
                }
            ),
            new ToolCallContext(),
            CancellationToken.None
        );

        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.IsError.Should().BeTrue();
        resolved.Payload.ErrorCode.Should().Be("queue_full");
    }

    [Fact]
    public async Task HandleAgentToolAsync_SpawnNameGateRejects_ReturnsRecoverableUnmatchedError()
    {
        // Option A: a host that correlates spawn results by an EXACT name (a workflow controller) supplies a
        // SpawnNameGate. When it rejects the spawn's name, the Agent handler must surface the correction as a
        // recoverable tool error (spawn_name_unmatched) — NOT throw and NOT spawn — so the caller re-issues the
        // exact name instead of looping on a silently-discarded duplicate. subagent_type is VALID here, proving
        // the rejection comes from the name gate, not from unknown_subagent_type.
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["researcher"] = new SubAgentTemplate
                {
                    SystemPrompt = "You are a researcher.",
                    Description = "Researches.",
                    AgentFactory = () => _subAgentMock.Object,
                },
            },
            SpawnNameGate = name =>
                name == "good:1:task" ? null : $"No workflow unit named '{name}'. Re-call Agent with good:1:task.",
        };
        var source = new MutableSubAgentTemplateSource(options.Templates);
        await using var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: source
        );
        var provider = new SubAgentToolProvider(manager, source);
        var handler = provider.GetFunctions().First(f => f.Contract.Name == "Agent").Handler;

        var args = JsonSerializer.Serialize(
            new
            {
                subagent_type = "researcher",
                prompt = "do it",
                name = "analyze",
            }
        );
        var result = await handler(args, new ToolCallContext(), CancellationToken.None);

        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.IsError.Should().BeTrue();
        resolved.Payload.ErrorCode.Should().Be("spawn_name_unmatched");
        resolved.Payload.Text.Should().Contain("good:1:task");
    }

    [Fact]
    public async Task HandleSendMessageToolAsync_MissingTarget_ThrowsArgumentException()
    {
        // Arrange
        var handler = GetHandler("SendMessage");
        var args = JsonSerializer.Serialize(new { prompt = "follow up" });

        // Act
        var act = () => handler(args, new ToolCallContext(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*target*required*");
    }

    [Fact]
    public async Task HandleSendMessageToolAsync_MissingPrompt_ThrowsArgumentException()
    {
        // Arrange
        var handler = GetHandler("SendMessage");
        var args = JsonSerializer.Serialize(new { target = "abc123" });

        // Act
        var act = () => handler(args, new ToolCallContext(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*prompt*required*");
    }

    [Fact]
    public async Task HandleCheckAgentToolAsync_MissingAgentId_ThrowsArgumentException()
    {
        // Arrange
        var handler = GetHandler("CheckAgent");
        var args = JsonSerializer.Serialize(new { });

        // Act
        var act = () => handler(args, new ToolCallContext(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*agent_id*required*");
    }

    [Fact]
    public async Task HandleAgentToolAsync_WorkflowSelectionOverridesPlaceholderOptionalModelValues()
    {
        SubAgentSpawnModelSelection? observed = null;
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["researcher"] = new SubAgentTemplate
                {
                    SystemPrompt = "research",
                    AgentFactory = () => _subAgentMock.Object,
                },
            },
            SpawnModelSelectionResolver = name =>
                name == "unit:1:task" ? new SubAgentSpawnModelSelection(Model: null, ModelIntelligence: null) : null,
            TierModelResolver = tier =>
            {
                observed = new SubAgentSpawnModelSelection(null, tier);
                return "tier-model";
            },
        };
        var source = new MutableSubAgentTemplateSource(options.Templates);
        await using var manager = new SubAgentManager(
            _parentMock.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            source
        );
        var provider = new SubAgentToolProvider(manager, source);
        var handler = provider.GetFunctions().First(f => f.Contract.Name == "Agent").Handler;
        var args = JsonSerializer.Serialize(
            new
            {
                subagent_type = "researcher",
                prompt = "work",
                name = "unit:1:task",
                model = "",
                modelIntelligence = 0,
            }
        );

        _ = await handler(args, new ToolCallContext(), CancellationToken.None);

        observed
            .Should()
            .BeNull(because: "the workflow unit's authoritative null tier must erase the LLM's placeholder zero");
    }

    [Fact]
    public async Task HandleAgentToolAsync_TypePolicyUsesCanonicalTypeAndOverridesCallerModelSelection()
    {
        string? observedType = null;
        int? observedTier = null;
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["code-reviewer:architecture-review"] = new SubAgentTemplate
                {
                    SystemPrompt = "architecture",
                    AgentFactory = () => _subAgentMock.Object,
                },
            },
            SpawnTypeModelSelectionResolver = subagentType =>
            {
                observedType = subagentType;
                return new SubAgentSpawnModelSelection(
                    Model: null,
                    ModelIntelligence: 5,
                    SelectionSource: "type-policy"
                );
            },
            TierModelResolver = tier =>
            {
                observedTier = tier;
                return "gpt-5.6-sol";
            },
        };
        var source = new MutableSubAgentTemplateSource(options.Templates);
        await using var manager = new SubAgentManager(
            _parentMock.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            source
        );
        var provider = new SubAgentToolProvider(manager, source);
        var handler = provider.GetFunctions().First(f => f.Contract.Name == "Agent").Handler;
        var args = JsonSerializer.Serialize(
            new
            {
                subagent_type = "architecture-review",
                prompt = "work",
                model = "gpt-5.6-luna",
                modelIntelligence = 1,
            }
        );

        _ = await handler(args, new ToolCallContext(), CancellationToken.None);

        observedType.Should().Be("code-reviewer:architecture-review");
        observedTier.Should().Be(5, "the mode policy must replace both caller-authored model fields");
        manager.ListAgents().Should().ContainSingle().Subject.ModelSelectionSource.Should().Be("type-policy");
    }

    [Fact]
    public async Task HandleAgentToolAsync_NullTypePolicyFallsThroughToWorkflowNamePolicy()
    {
        int? observedTier = null;
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["researcher"] = new SubAgentTemplate
                {
                    SystemPrompt = "research",
                    AgentFactory = () => _subAgentMock.Object,
                },
            },
            SpawnTypeModelSelectionResolver = _ => null,
            SpawnModelSelectionResolver = name =>
                name == "unit:1:task" ? new SubAgentSpawnModelSelection(Model: null, ModelIntelligence: 4) : null,
            TierModelResolver = tier =>
            {
                observedTier = tier;
                return "tier-model";
            },
        };
        var source = new MutableSubAgentTemplateSource(options.Templates);
        await using var manager = new SubAgentManager(
            _parentMock.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            source
        );
        var provider = new SubAgentToolProvider(manager, source);
        var handler = provider.GetFunctions().First(f => f.Contract.Name == "Agent").Handler;
        var args = JsonSerializer.Serialize(
            new
            {
                subagent_type = "researcher",
                prompt = "work",
                name = "unit:1:task",
                model = "caller-model",
                modelIntelligence = 1,
            }
        );

        _ = await handler(args, new ToolCallContext(), CancellationToken.None);

        observedTier
            .Should()
            .Be(4, "a non-applicable type policy must not suppress an authoritative workflow-unit selection");
    }

    [Fact]
    public void ForChildLoop_PreservesConversationTypePolicyAndClearsWorkflowLocalPolicy()
    {
        Func<string, SubAgentSpawnModelSelection?> typePolicy = _ => new SubAgentSpawnModelSelection(
            null,
            3,
            "type-policy"
        );
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>(),
            SpawnTypeModelSelectionResolver = typePolicy,
            SpawnModelSelectionResolver = _ => new SubAgentSpawnModelSelection(null, 5, "workflow-unit"),
            SpawnNameGate = _ => "workflow-local",
            SpawnMetadataResolver = _ => new SubAgentSpawnMetadata("role", "description"),
        };

        var childOptions = options.ForChildLoop();

        childOptions.SpawnTypeModelSelectionResolver.Should().BeSameAs(typePolicy);
        childOptions.SpawnModelSelectionResolver.Should().BeNull();
        childOptions.SpawnNameGate.Should().BeNull();
        childOptions.SpawnMetadataResolver.Should().BeNull();
    }

    [Fact]
    public async Task HandleAgentToolAsync_OrdinaryHostPreservesIntentionalTierZero()
    {
        int? observedTier = null;
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["researcher"] = new SubAgentTemplate
                {
                    SystemPrompt = "research",
                    AgentFactory = () => _subAgentMock.Object,
                },
            },
            TierModelResolver = tier =>
            {
                observedTier = tier;
                return "tier-model";
            },
        };
        var source = new MutableSubAgentTemplateSource(options.Templates);
        await using var manager = new SubAgentManager(
            _parentMock.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            source
        );
        var provider = new SubAgentToolProvider(manager, source);
        var handler = provider.GetFunctions().First(f => f.Contract.Name == "Agent").Handler;
        var args = JsonSerializer.Serialize(
            new
            {
                subagent_type = "researcher",
                prompt = "work",
                modelIntelligence = 0,
            }
        );

        _ = await handler(args, new ToolCallContext(), CancellationToken.None);

        observedTier.Should().Be(0);
    }

    [Fact]
    public void GetFunctions_CalledTwiceWithUnchangedInputs_ReturnsTheSameDescriptorInstances()
    {
        // ToolCallInjectionMiddleware re-invokes the function-set factory on EVERY LLM call, so this
        // provider rebuilt the whole sub-agent surface — template catalog text and all — once per turn
        // from inputs that change perhaps twice in a session. Serving the previous build while its
        // inputs are unchanged is the win; GetFunctions_AfterTryRegister_ReflectsNewTemplate,
        // SuppressSpawning_DropsOnlyTheSpawnContract and
        // GetFunctions_BuiltBeforeSuppression_DoesNotServeTheSpawnToolInsideTheScope are the mutation
        // guards that it still rebuilds when they do change — the last two pinning the suppression
        // key in each direction separately.
        var first = _provider!.GetFunctions().ToList();
        var second = _provider!.GetFunctions().ToList();

        second.Should().Equal(first, (a, b) => ReferenceEquals(a, b));
    }

    [Fact]
    public void GetFunctions_AfterTryRegister_ReflectsNewTemplate()
    {
        // Mid-session activation contract (#77): when the discovery webhook registers a new
        // template into the shared source, the next GetFunctions() call must surface it in the
        // Agent tool's catalog. The ToolCallInjectionMiddleware re-invokes the function-set
        // factory each request, so this provider must not serve a memoized descriptor list across
        // a template change — the memo is keyed on the source's snapshot reference precisely so a
        // TryRegister invalidates it.
        var beforeRegister = _provider!.GetFunctions().First(f => f.Contract.Name == "Agent").Contract.Description!;
        beforeRegister.Should().NotContain("reviewer");

        _source!
            .TryRegister(
                "reviewer",
                new SubAgentTemplate
                {
                    Name = "reviewer",
                    SystemPrompt = "You are a reviewer.",
                    Description = "Reviews pull requests for correctness.",
                    WhenToUse = "Use after coder completes a change.",
                    AgentFactory = () => _subAgentMock.Object,
                }
            )
            .Should()
            .BeTrue();

        var afterRegister = _provider!.GetFunctions().First(f => f.Contract.Name == "Agent").Contract.Description!;
        afterRegister.Should().Contain("reviewer");
        afterRegister.Should().Contain("Reviews pull requests for correctness.");
        afterRegister.Should().Contain("Use after coder completes a change.");
    }

    [Fact]
    public void AgentDescriptor_SubagentTypeEnumList_IncludesNewlyRegistered()
    {
        // The subagent_type parameter description carries a comma-separated enum list of
        // available template keys; this must also reflect a TryRegister-added template so the
        // parent LLM knows it can pick the new type.
        _source!.TryRegister(
            "reviewer",
            new SubAgentTemplate
            {
                Name = "reviewer",
                SystemPrompt = "You are a reviewer.",
                Description = "Reviews PRs.",
                WhenToUse = "After coder.",
                AgentFactory = () => _subAgentMock.Object,
            }
        );

        var subagentTypeDesc = _provider!
            .GetFunctions()
            .First(f => f.Contract.Name == "Agent")
            .Contract.Parameters!.First(p => p.Name == "subagent_type")
            .Description!;

        subagentTypeDesc.Should().Contain("researcher");
        subagentTypeDesc.Should().Contain("coder");
        subagentTypeDesc.Should().Contain("reviewer");
    }

    [Fact]
    public void SuppressSpawning_DropsOnlyTheSpawnContract()
    {
        // The synthesis turn of a recursive review must not be able to start NEW children, but it must
        // still be able to read what the children delivered (CheckAgent) and nudge one (SendMessage).
        var provider = _provider!;

        using (provider.SuppressSpawning())
        {
            provider
                .GetFunctions()
                .Select(f => f.Contract.Name)
                .Should()
                .BeEquivalentTo(["SendMessage", "CheckAgent", "WaitAgent"]);
        }

        // Suppression is scoped: the very next contract build advertises spawning again.
        provider
            .GetFunctions()
            .Select(f => f.Contract.Name)
            .Should()
            .BeEquivalentTo(["Agent", "SendMessage", "CheckAgent", "WaitAgent"]);
    }

    [Fact]
    public void GetFunctions_BuiltBeforeSuppression_DoesNotServeTheSpawnToolInsideTheScope()
    {
        // The memo's OVER-granting direction, pinned on its own rather than by shared implication.
        // SuppressSpawning_DropsOnlyTheSpawnContract builds its first surface INSIDE the scope, so it
        // exercises only the way OUT (suppressed -> not), where a stale memo under-grants. This is the way
        // IN: an ordinary turn populates the memo WITH the spawn tool, and the next build — now inside a
        // scope — must not serve that entry back. Both directions happen to ride on the same equality
        // conjunct in the memo key today, so one mutation reddens both; that is an implementation
        // coincidence, and this is the direction that fails OPEN, handing the spawn tool to exactly the
        // turn a caller was promised could not start new children.
        var provider = _provider!;

        // Premise, not the subject: without an unsuppressed entry actually in the memo, the assertion
        // below would pass for the wrong reason.
        provider
            .GetFunctions()
            .Select(f => f.Contract.Name)
            .Should()
            .Contain("Agent", "the memo has to hold a build that CARRIES the spawn tool for this to mean anything");

        using (provider.SuppressSpawning())
        {
            provider
                .GetFunctions()
                .Select(f => f.Contract.Name)
                .Should()
                .BeEquivalentTo(["SendMessage", "CheckAgent", "WaitAgent"]);
        }
    }

    [Fact]
    public async Task SuppressSpawning_RefusesTheSpawnHandlerIfTheModelCallsItAnyway()
    {
        // Contracts are rebuilt per turn but tool HANDLERS are a construction-time snapshot on the loop,
        // so hiding the contract alone still leaves the handler reachable if the model replays an Agent
        // call from earlier history. The handler must refuse rather than start a child after the barrier.
        var handler = GetHandler("Agent");
        var args = JsonSerializer.Serialize(new { subagent_type = "researcher", prompt = "go" });

        using var suppression = _provider!.SuppressSpawning();
        var result = await handler(args, new ToolCallContext(), CancellationToken.None);

        var payload = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be("spawn_suppressed");
    }

    [Fact]
    public void SuppressSpawning_IsReentrantAndIdempotentOnDispose()
    {
        var provider = _provider!;
        var outer = provider.SuppressSpawning();
        var inner = provider.SuppressSpawning();

        // Double-dispose of the inner scope must not decrement the depth twice, which would
        // re-advertise spawning while the outer scope still expects it hidden.
        inner.Dispose();
        inner.Dispose();
        provider
            .GetFunctions()
            .Select(f => f.Contract.Name)
            .Should()
            .NotContain("Agent", "the outer scope is still open");

        outer.Dispose();
        provider.GetFunctions().Select(f => f.Contract.Name).Should().Contain("Agent");
    }

    private ToolHandler GetHandler(string name)
    {
        return _provider!.GetFunctions().First(f => f.Contract.Name == name).Handler;
    }
}
