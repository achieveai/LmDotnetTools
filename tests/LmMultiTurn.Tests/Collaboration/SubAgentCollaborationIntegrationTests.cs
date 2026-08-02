using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// Behavioural tests for collaboration wired through the sub-agent stack: the tool surface it
/// swaps in, the admission rules it enforces at spawn time, recursive delegation across independent
/// managers, and the typed any-to-any messaging the new tools expose.
/// </summary>
/// <remarks>
/// The pairing that matters here is that every collaboration assertion has a legacy counterpart:
/// absence of <see cref="AgentCollaborationSetup"/> is the feature gate, so a test proving the new
/// behaviour is only meaningful next to one proving the old behaviour is untouched.
/// </remarks>
public class SubAgentCollaborationIntegrationTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly Mock<IStreamingAgent> _subAgentMock = new();
    private readonly List<SubAgentManager> _managers = [];

    public Task InitializeAsync()
    {
        _ = _parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        SetupSubAgentReply("done");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            await manager.DisposeAsync();
        }
    }

    #region Tool surface

    [Fact]
    public void GetFunctions_WithoutCollaboration_KeepsLegacySurfaceExactly()
    {
        var (_, provider) = CreateManager(collaboration: null);

        var functions = provider.GetFunctions().ToList();

        functions.Select(f => f.Contract.Name)
            .Should().BeEquivalentTo(["Agent", "SendMessage", "CheckAgent"]);

        var agentParams = functions.First(f => f.Contract.Name == "Agent").Contract.Parameters!;
        agentParams.Select(p => p.Name).Should().NotContain("role");
        agentParams.First(p => p.Name == "description").IsRequired.Should().BeFalse();

        functions.First(f => f.Contract.Name == "SendMessage").Contract.Parameters!
            .Select(p => p.Name)
            .Should().BeEquivalentTo(["target", "prompt", "run_in_background"]);
    }

    [Fact]
    public void GetFunctions_WithCollaboration_SwapsInTheCollaborationSurface()
    {
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var names = provider.GetFunctions().Select(f => f.Contract.Name).ToList();

        names.Should().BeEquivalentTo(
            ["Agent", "CheckAgents", "WaitForAgents", "GetAgents", "SendMessage"]);
        names.Should().NotContain("CheckAgent");
    }

    [Fact]
    public void GetFunctions_AtDelegationLimit_HidesDelegationToolsButKeepsMessaging()
    {
        // Depth 0 means "this collaboration exists, but nobody may spawn". An agent that cannot
        // delegate must still be able to find and message the agents that already exist.
        var (_, provider) = CreateManager(
            CreateRegisteredRoot(new AgentCollaborationOptions { MaxDelegationDepth = 0 }));

        provider.GetFunctions().Select(f => f.Contract.Name)
            .Should().BeEquivalentTo(["GetAgents", "SendMessage"]);
    }

    [Fact]
    public void AgentDescriptor_WithCollaboration_RequiresDirectoryMetadata()
    {
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var parameters = provider.GetFunctions()
            .First(f => f.Contract.Name == "Agent").Contract.Parameters!;

        parameters.Select(p => p.Name).Should().Contain("role");
        parameters.First(p => p.Name == "description").IsRequired.Should().BeTrue();
    }

    #endregion

    #region Admission

    [Fact]
    public async Task Spawn_WithoutRole_IsRefusedWithAnActionableCode()
    {
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var payload = await InvokeAsync(provider, "Agent", new
        {
            subagent_type = "worker",
            prompt = "work",
            description = "Handles the migration.",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(SubAgentCollaborationFailureCodes.InvalidRole);
    }

    [Fact]
    public async Task Spawn_WithoutDescription_IsRefusedWithAnActionableCode()
    {
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var payload = await InvokeAsync(provider, "Agent", new
        {
            subagent_type = "worker",
            prompt = "work",
            role = "migrator",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(SubAgentCollaborationFailureCodes.InvalidDescription);
    }

    [Fact]
    public async Task Spawn_WithoutRole_IsAcceptedWhenTheTemplatePinsItsOwn()
    {
        // A Fixed-role template is the host asserting what this agent is, so asking the model to
        // restate it would only create a way for the two to disagree.
        var (manager, provider) = CreateManager(
            CreateRegisteredRoot(),
            template: new SubAgentTemplate
            {
                SystemPrompt = "You are a reviewer.",
                Role = "code reviewer",
                RoleMode = SubAgentRoleMode.Fixed,
                AgentFactory = () => _subAgentMock.Object,
            });

        var payload = await InvokeAsync(provider, "Agent", new
        {
            subagent_type = "worker",
            prompt = "review",
            description = "Reviews the auth change.",
            run_in_background = true,
        });

        payload.IsError.Should().BeFalse();
        manager.Collaboration!.Directory.Snapshot()
            .Should().ContainSingle(e => e.Role == "code reviewer");
    }

    [Fact]
    public async Task Spawn_PublishesTheChildIntoTheSharedDirectory()
    {
        var root = CreateRegisteredRoot();
        var (manager, provider) = CreateManager(root);

        _ = await InvokeAsync(provider, "Agent", new
        {
            subagent_type = "worker",
            prompt = "work",
            role = "migrator",
            description = "Owns the auth migration.",
            name = "auth-migrator",
            run_in_background = true,
        });

        var child = manager.Collaboration!.Directory.Snapshot()
            .Should().ContainSingle(e => e.Name == "auth-migrator").Subject;

        child.Role.Should().Be("migrator");
        child.Description.Should().Be("Owns the auth migration.");
        child.ParentAgentId.Should().Be(root.AgentId);
        child.DelegationDepth.Should().Be(1);
        child.AgentType.Should().Be("worker");
    }

    [Fact]
    public async Task Spawn_BeyondRootCapacity_IsRefusedRatherThanQueued()
    {
        // The root-wide cap is what per-manager concurrency cannot express. It must bite even though
        // this manager's own gate would happily admit a second agent.
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxTotalAgents = 1 });
        var (_, provider) = CreateManager(root);

        _ = await InvokeAsync(provider, "Agent", NewSpawn("first"));
        var second = await InvokeAsync(provider, "Agent", NewSpawn("second"));

        second.IsError.Should().BeTrue();
        second.ErrorCode.Should().Be(SubAgentCollaborationFailureCodes.CapacityExhausted);
    }

    [Fact]
    public async Task DisposingAManager_ReturnsTheCapacityItsChildrenHeld()
    {
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxTotalAgents = 1 });
        var (manager, provider) = CreateManager(root);

        _ = await InvokeAsync(provider, "Agent", NewSpawn("first"));
        await manager.DisposeAsync();

        var (_, second) = CreateManager(root);
        var result = await InvokeAsync(second, "Agent", NewSpawn("after-dispose"));

        result.IsError.Should().BeFalse();
        root.Directory.FindById(root.AgentId).Should().NotBeNull(
            because: "retiring a child must not disturb the agent that spawned it");
    }

    [Fact]
    public async Task Spawn_PastTheDelegationLimit_IsRefusedEvenWhenTheToolIsNotAdvertised()
    {
        // Hiding the tool is guidance, not enforcement: a model can call a tool it was told about on
        // an earlier turn, so the depth rule has to be re-checked at admission.
        var root = CreateRegisteredRoot();
        var (parentManager, provider) = CreateManager(root);

        var childId = await SpawnAndResolveIdAsync(provider);
        var childSetup = parentManager.GetChildCollaboration(childId)!;
        childSetup.CanDelegate.Should().BeFalse();

        var (grandchildManager, _) = CreateManager(childSetup);

        var act = () => grandchildManager.SpawnAsync(
            "worker", "work", role: "deeper", description: "Should never exist.");

        (await act.Should().ThrowAsync<SubAgentCollaborationException>()).Which
            .FailureCode.Should().Be(SubAgentCollaborationFailureCodes.DepthLimit);
    }

    #endregion

    #region Recursive delegation

    [Fact]
    public async Task ARaisedDelegationLimit_LetsAChildSpawnThroughItsOwnManager()
    {
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxDelegationDepth = 2 });
        var (parentManager, parentProvider) = CreateManager(root);

        var childId = await SpawnAndResolveIdAsync(parentProvider);
        var childSetup = parentManager.GetChildCollaboration(childId)!;
        childSetup.CanDelegate.Should().BeTrue();

        // A separate manager, exactly as the child's own loop would own: the parent's gate, queue,
        // and agent table are not shared with it, only the collaboration is.
        var (grandchildManager, childProvider) = CreateManager(childSetup);
        grandchildManager.Should().NotBeSameAs(parentManager);
        childProvider.GetFunctions().Select(f => f.Contract.Name).Should().Contain("Agent");

        _ = await InvokeAsync(childProvider, "Agent", new
        {
            subagent_type = "worker",
            prompt = "deep work",
            role = "specialist",
            description = "Does the deepest piece.",
            name = "specialist",
            run_in_background = true,
        });

        var grandchild = root.Directory.Snapshot()
            .Should().ContainSingle(e => e.Name == "specialist").Subject;

        grandchild.DelegationDepth.Should().Be(2);
        grandchild.ParentAgentId.Should().Be(childId);
        grandchild.AncestorAgentIds.Should().ContainInOrder(root.AgentId, childId);
    }

    #endregion

    #region GetAgents

    [Fact]
    public async Task GetAgents_ListsTheWholeCollaborationAndMarksTheCaller()
    {
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);
        _ = await InvokeAsync(provider, "Agent", NewSpawn("peer"));

        var payload = await InvokeAsync(provider, "GetAgents", new { });

        payload.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("your_agent_id").GetString().Should().Be(root.AgentId);

        var agents = doc.RootElement.GetProperty("agents").EnumerateArray().ToList();
        agents.Should().HaveCount(2);
        agents.Count(a => a.GetProperty("is_you").GetBoolean()).Should().Be(1);
        agents.Should().Contain(a => a.GetProperty("name").GetString() == "peer");
    }

    #endregion

    #region SendMessage

    [Fact]
    public async Task SendMessage_UnderCollaboration_AdmitsAndDeliversToAnyRegisteredAgent()
    {
        var root = CreateRegisteredRoot();
        var (peerEndpoint, _) = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new
        {
            target = "helper",
            content = "What does the auth flag default to?",
            msg_type = "question",
        });

        payload.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("to_name").GetString().Should().Be("helper");
        doc.RootElement.GetProperty("expects_reply").GetBoolean().Should().BeTrue();

        var delivered = await peerEndpoint.Received.WaitAsync(TimeSpan.FromSeconds(10));
        delivered.FromAgentId.Should().Be(root.AgentId);
        delivered.AgentMessageType.Should().Be(AgentMessageType.Question);
    }

    [Fact]
    public async Task SendMessage_ToAnUnknownTarget_IsRecoverableRatherThanFatal()
    {
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var payload = await InvokeAsync(provider, "SendMessage", new
        {
            target = "nobody",
            content = "hello",
            msg_type = "task_update",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentDirectoryFailureCodes.NotFound);
        payload.Text.Should().Contain("GetAgents");
    }

    [Fact]
    public async Task SendMessage_ToARetiredAgent_SaysItFinishedRatherThanThatItNeverExisted()
    {
        // The two refusals need different words: a wrong name is the sender's mistake to correct,
        // whereas a retired agent is a real one whose work is simply over.
        var root = CreateRegisteredRoot();
        var (_, peerSetup) = RegisterPeer(root, "helper");
        _ = root.Bundle.RetireAgent(peerSetup.AgentId, AgentCollaborationStatuses.Completed);
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new
        {
            target = "helper",
            content = "hello",
            msg_type = "task_update",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.UnknownTarget);
        payload.Text.Should().Contain("finished");
    }

    [Fact]
    public async Task SendMessage_ToSelf_IsRefused()
    {
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new
        {
            target = root.AgentId,
            content = "hello",
            msg_type = "task_update",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.SelfDelivery);
    }

    [Fact]
    public async Task SendMessage_ResponseWithoutCorrelation_IsRefusedBeforeAdmission()
    {
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new
        {
            target = "helper",
            content = "the answer",
            msg_type = "response",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be("missing_correlation");
    }

    [Fact]
    public async Task SendMessage_WithAnUnknownType_ListsTheTypesThatExist()
    {
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new
        {
            target = "helper",
            content = "hello",
            msg_type = "shout",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be("invalid_msg_type");
        payload.Text.Should().Contain("delegate_task");
    }

    #endregion

    #region CheckAgents and WaitForAgents

    [Fact]
    public async Task CheckAgents_ObservesEveryListedAgentInOneCall()
    {
        var (_, provider) = CreateManager(CreateRegisteredRoot());
        var first = await SpawnAndResolveIdAsync(provider, "one");
        var second = await SpawnAndResolveIdAsync(provider, "two");

        var payload = await InvokeAsync(provider, "CheckAgents", new
        {
            agent_ids = $"{first},{second}",
        });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("requested").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("not_found").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("agents").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task WaitForAgents_ReturnsOnceTheListedChildrenFinish()
    {
        var (_, provider) = CreateManager(CreateRegisteredRoot());
        var agentId = await SpawnAndResolveIdAsync(provider);

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");
        doc.RootElement.GetProperty("agents").GetProperty("requested").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task WaitForAgents_OnAnUnresolvableTarget_RefusesTheWholeCall()
    {
        var (_, provider) = CreateManager(CreateRegisteredRoot());
        var agentId = await SpawnAndResolveIdAsync(provider);

        var payload = await InvokeAsync(provider, "WaitForAgents", new
        {
            agent_ids = $"{agentId},ghost",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be("unknown_agent");
        payload.Text.Should().Contain("ghost");
    }

    [Fact]
    public async Task WaitForAgents_IsInterruptedByAQuestionAddressedToTheWaiter()
    {
        // A waiting agent that cannot be reached is a deadlock waiting to happen: whoever needs an
        // answer from it would block until the wait it is doing finishes, which may need that answer.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root, template: BlockingTemplate());
        var agentId = await SpawnAndResolveIdAsync(provider);
        var (_, peerSetup) = RegisterPeer(root, "asker");

        var dispatch = new AgentCollaborationMessenger(peerSetup).Send(
            root.AgentId, "Which branch?", AgentMessageType.Question);
        dispatch.Result.Succeeded.Should().BeTrue();

        // Settle the delivery before waiting, so the question is unambiguously open by the time the
        // sweep runs rather than racing a background delivery that would close it on failure.
        await dispatch.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("question_received");
        doc.RootElement.GetProperty("question").GetProperty("from_name").GetString()
            .Should().Be("asker");
    }

    [Fact]
    public async Task WaitForAgents_IsInterruptedByAQuestionThatArrivesAfterTheWaitBegins()
    {
        // The mirror ordering of the test above. The wait subscribes and sweeps synchronously before
        // it suspends, so a question admitted after the call starts is caught by the subscription
        // rather than the sweep — the path that would otherwise hang here, with no timeout to rescue
        // it.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root, template: BlockingTemplate());
        var agentId = await SpawnAndResolveIdAsync(provider);
        var (_, peerSetup) = RegisterPeer(root, "asker");

        var wait = InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId });
        var dispatch = new AgentCollaborationMessenger(peerSetup).Send(
            root.AgentId, "Which branch?", AgentMessageType.Question);
        dispatch.Result.Succeeded.Should().BeTrue();

        var payload = await wait;

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("question_received");
    }

    [Fact]
    public async Task WaitForAgents_OnExpiry_ReportsTimeoutWithoutStoppingAnything()
    {
        var (manager, provider) = CreateManager(CreateRegisteredRoot(), template: BlockingTemplate());
        var agentId = await SpawnAndResolveIdAsync(provider);

        var payload = await InvokeAsync(provider, "WaitForAgents", new
        {
            agent_ids = agentId,
            timeout_seconds = 1,
        });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("timeout");
        manager.TryPeek(agentId, out _).Should().BeTrue(
            because: "a wait that expires abandons the observation, never the agent");
    }

    #endregion

    #region Helpers

    private static object NewSpawn(string name) => new
    {
        subagent_type = "worker",
        prompt = "work",
        role = "worker role",
        description = "Does a unit of work.",
        name,
        run_in_background = true,
    };

    /// <summary>
    /// Creates a root handle and publishes it, as <see cref="MultiTurnAgentLoop"/> does for a real
    /// root. Registration cannot be deferred: the directory refuses a child whose parent is absent.
    /// </summary>
    /// <remarks>
    /// The root gets a real endpoint because a message to an endpoint-less agent fails delivery and
    /// closes its own ledger entry, which would silently turn "sent a question to the root" into
    /// "there is no open question" a moment later.
    /// </remarks>
    private static AgentCollaborationSetup CreateRegisteredRoot(
        AgentCollaborationOptions? options = null)
    {
        var setup = AgentCollaborationSetup.CreateRoot(options ?? new AgentCollaborationOptions());
        _ = setup.Directory.TryRegister(
            setup.Context,
            setup.Name,
            AgentCollaborationStatuses.Running,
            new RecordingEndpoint());
        return setup;
    }

    private (RecordingEndpoint Endpoint, AgentCollaborationSetup Setup) RegisterPeer(
        AgentCollaborationSetup root,
        string name)
    {
        var context = root.Context.CreateChild(
            $"agent-{name}", AgentKind.SubAgent, $"{name} role", $"Stands in for {name}.");
        var endpoint = new RecordingEndpoint();

        _ = root.Directory.TryAcquireCapacity(context.AgentId);
        root.Directory
            .TryRegister(context, name, AgentCollaborationStatuses.Running, endpoint)
            .Succeeded.Should().BeTrue();

        return (endpoint, root.ForChild(context, name));
    }

    private (SubAgentManager Manager, SubAgentToolProvider Provider) CreateManager(
        AgentCollaborationSetup? collaboration,
        SubAgentTemplate? template = null)
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = template
                    ?? new SubAgentTemplate
                    {
                        SystemPrompt = "You are a worker.",
                        Description = "Does work.",
                        AgentFactory = () => _subAgentMock.Object,
                    },
            },
            MaxConcurrentSubAgents = 5,
        };

        var source = new MutableSubAgentTemplateSource(options.Templates);
        var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: source,
            collaboration: collaboration);

        _managers.Add(manager);
        return (manager, new SubAgentToolProvider(manager, source));
    }

    private SubAgentTemplate BlockingTemplate() => new()
    {
        SystemPrompt = "You are a worker.",
        AgentFactory = () =>
        {
            var mock = new Mock<IStreamingAgent>();
            _ = mock
                .Setup(a => a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()))
                .Returns<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                    (_, _, ct) => Task.FromResult(BlockingStream(ct)));
            return mock.Object;
        },
    };

    private async Task<string> SpawnAndResolveIdAsync(
        SubAgentToolProvider provider,
        string name = "child")
    {
        var payload = await InvokeAsync(provider, "Agent", NewSpawn(name));
        payload.IsError.Should().BeFalse(payload.Text);

        using var doc = JsonDocument.Parse(payload.Text);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private static async Task<ToolHandlerResultPayload> InvokeAsync(
        SubAgentToolProvider provider,
        string toolName,
        object args)
    {
        var handler = provider.GetFunctions().First(f => f.Contract.Name == toolName).Handler;
        var result = await handler(
            JsonSerializer.Serialize(args), new ToolCallContext(), CancellationToken.None);

        return result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
    }

    private void SetupSubAgentReply(string text)
    {
        _ = _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable(
                [new TextMessage { Text = text, Role = Role.Assistant }])));
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<IMessage> BlockingStream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    /// <summary>A stand-in for another agent's owner, so a delivery can be observed without a loop.</summary>
    private sealed class RecordingEndpoint : IAgentWriteEndpoint
    {
        private readonly TaskCompletionSource<AgentMessage> _received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AgentMessage> Received => _received.Task;

        public ValueTask<AgentDeliveryOutcome> DeliverAsync(
            AgentMessage message,
            CancellationToken cancellationToken = default)
        {
            _ = _received.TrySetResult(message);
            return ValueTask.FromResult(
                new AgentDeliveryOutcome(AgentDeliveryDisposition.Delivered));
        }
    }

    #endregion
}
