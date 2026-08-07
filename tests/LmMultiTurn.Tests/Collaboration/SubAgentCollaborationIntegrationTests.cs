using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
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
            .Should().BeEquivalentTo(["Agent", "SendMessage", "CheckAgent", "WaitAgent"]);

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
        names.Should().NotContain(
            "WaitAgent",
            "the singular legacy wait is replaced by WaitForAgents — no alias is added under collaboration");
    }

    [Fact]
    public void GetFunctions_AtDelegationLimit_HidesOnlySpawningAndKeepsCollaboration()
    {
        // Depth 0 means "this collaboration exists, but nobody may spawn". Only Agent goes: an agent
        // that cannot delegate must still find, message, observe, and wait on the agents that already
        // exist, and hiding CheckAgents or WaitForAgents would leave it able to ask a question it
        // could never notice the answer to.
        var (_, provider) = CreateManager(
            CreateRegisteredRoot(new AgentCollaborationOptions { MaxDelegationDepth = 0 }));

        provider.GetFunctions().Select(f => f.Contract.Name)
            .Should().BeEquivalentTo(
                ["CheckAgents", "WaitForAgents", "GetAgents", "SendMessage"]);
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

    [Fact]
    public void AgentDescriptor_WarnsThatRoleAndDescriptionAreVisibleToEveryone()
    {
        // Both fields are published into a directory every agent can read, so the model has to be told
        // before it writes them — a warning added afterwards cannot un-share what was already put there.
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var parameters = provider.GetFunctions()
            .First(f => f.Contract.Name == "Agent").Contract.Parameters!;

        foreach (var name in new[] { "role", "description" })
        {
            parameters.First(p => p.Name == name).Description
                .Should().Contain("secrets").And.Contain("customer data");
        }
    }

    [Fact]
    public void SendMessageDescriptor_ConstrainsMsgTypeToTheKindsThatExist()
    {
        // An open string invites a kind the handler will only reject after the fact. The enum spends
        // the model's mistake at schema time, where it costs nothing.
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var msgType = provider.GetFunctions()
            .First(f => f.Contract.Name == "SendMessage").Contract.Parameters!
            .First(p => p.Name == "msg_type");

        msgType.ParameterType!.Enum.Should().BeEquivalentTo(
            ["question", "delegate_task", "task_update", "steer", "response"]);
    }

    [Fact]
    public void WaitForAgentsDescriptor_SaysBothWhenToWaitAndWhenNotTo()
    {
        // Waiting is the one collaboration tool that costs the caller its turn, so the description has
        // to rule cases out as explicitly as it rules them in.
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var description = provider.GetFunctions()
            .First(f => f.Contract.Name == "WaitForAgents").Contract.Description!;

        description.Should().Contain("WHEN TO USE IT").And.Contain("WHEN NOT TO USE IT");
    }

    /// <summary>
    /// BOTH waits must name the id namespace they accept, and the pin covers both in one test so they
    /// cannot drift apart.
    /// </summary>
    /// <remarks>
    /// WaitAgent carried this guidance first, but WaitAgent is the LEGACY tool: the Workspace Agent —
    /// the one mode where collaboration now defaults on, and the only surface where StartWorkflowAgent
    /// sits beside the Agent tool handing out ids that look identical — sees WaitForAgents instead. A
    /// warning that appears only on the surface the affected model never loads is no warning at all.
    /// </remarks>
    [Fact]
    public void BothWaits_TellTheModelThatWorkflowIdsAreADifferentNamespace()
    {
        var (_, legacy) = CreateManager(collaboration: null);
        var (_, collaborative) = CreateManager(CreateRegisteredRoot());

        var waitAgent = Description(legacy, "WaitAgent");
        var waitForAgents = Description(collaborative, "WaitForAgents");

        waitAgent.Should().Contain("Use an `agent_id` returned by `Agent`; do not pass workflow IDs.");
        waitForAgents.Should().Contain("Use `agent_ids` returned by `Agent`")
            .And.Contain("do not pass workflow IDs.");

        // The redirect is the actionable half — "not this tool" only helps if it names the one that
        // does work — so it is shared verbatim rather than paraphrased per descriptor.
        waitAgent.Should().Contain(SubAgentToolProvider.WorkflowIdRedirect);
        waitForAgents.Should().Contain(SubAgentToolProvider.WorkflowIdRedirect);
        SubAgentToolProvider.WorkflowIdRedirect.Should().Contain("WaitWorkflow");
    }

    private static string Description(SubAgentToolProvider provider, string toolName) =>
        provider.GetFunctions().Single(f => f.Contract.Name == toolName).Contract.Description!;

    #endregion

    #region Admission

    [Fact]
    public async Task Spawn_WithARoleThatContradictsAPinnedTemplate_IsRefused()
    {
        // Silently overriding the caller's role would leave the model believing it had labelled the
        // child one thing while every other agent saw another.
        var (_, provider) = CreateManager(
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
            role = "release manager",
            description = "Reviews the auth change.",
            run_in_background = true,
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(SubAgentCollaborationFailureCodes.InvalidRole);
    }

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
    public async Task Spawn_ThatFailsAfterAdmission_ReturnsTheRootCapacityItHadTaken()
    {
        // Capacity is taken at admission, before the agent is built, so everything that can go wrong
        // while building it happens holding a permit. A construction failure is the ordinary case: a
        // bad model identifier or an unreachable provider throws from the factory, and if that path
        // forgets the permit the collaboration silently shrinks by one agent for the rest of its life.
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxTotalAgents = 1 });
        var (_, provider) = CreateManager(root, FailingTemplate());

        var spawn = async () => await InvokeAsync(provider, "Agent", NewSpawn("doomed"));

        _ = await spawn.Should().ThrowAsync<InvalidOperationException>();
        root.Directory.Capacity.InUse.Should()
            .Be(0, "a spawn that never produced an agent is not occupying a slot");
        root.Directory.FindById(root.AgentId).Should().NotBeNull();

        // The permit accounting is only worth anything if the next spawn can actually use it, so the
        // proof is a real admission rather than a counter that happens to read zero.
        var (_, healthy) = CreateManager(root);
        (await InvokeAsync(healthy, "Agent", NewSpawn("after-failure"))).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Spawn_CancelledWhileQueuedBehindASaturatedLocalGate_ReclaimsRootCapacityAndRetiresTheDirectoryEntry()
    {
        // Admission (a root-wide capacity lease and a "queued" directory row) happens inside
        // SpawnAsync BEFORE the per-manager concurrency gate is even consulted, so a spawn that never
        // gets a LOCAL slot has already taken both root-wide resources. The previous test proves this
        // is reclaimed when the agent fails to construct; this proves the other pre-start exit — a
        // foreground caller cancelling while its spawn is queued behind a saturated local gate — gives
        // both back too. Left unreclaimed, a cancelled-and-retried queued spawn permanently shrinks the
        // collaboration's capacity by one, even though nothing it ever queued behind actually ran.
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxTotalAgents = 5 });
        var (manager, _) = CreateManager(
            root,
            template: BlockingTemplate(),
            configure: options => options with { MaxConcurrentSubAgents = 1 });

        // Occupies the only local slot forever (BlockingTemplate never completes), so the gate the
        // second spawn queues behind can never free up on its own.
        _ = await manager.SpawnAsync(
            "worker",
            "work",
            name: "first",
            role: "worker role",
            description: "Holds the only local slot.",
            runInBackground: true);

        root.Directory.Capacity.InUse.Should().Be(1);

        // Foreground (no run_in_background), so this call itself awaits the queue rather than
        // returning a receipt immediately — exactly the caller shape the leaked-admission bug needs.
        // Everything up to that await, including AdmitToCollaboration, runs synchronously on this
        // call, so both assertions below observe it without any polling.
        using var cts = new CancellationTokenSource();
        var queuedSpawn = manager.SpawnAsync(
            "worker",
            "work",
            name: "second",
            role: "worker role",
            description: "Never gets a local slot.",
            ct: cts.Token);

        root.Directory.Capacity.InUse.Should().Be(
            2, "the queued spawn already holds a root-wide lease even though it never got a local slot");
        root.Directory.Resolve("second").Entry!.Status.Should().Be(AgentCollaborationStatuses.Queued);

        cts.Cancel();
        var act = async () => await queuedSpawn;
        _ = await act.Should().ThrowAsync<OperationCanceledException>();

        // The pump retires the admission before it unblocks the caller's await (see
        // SubAgentManager.CancelQueuedSpawn), so both halves are already reclaimed by the time the
        // cancellation has been observed above — nothing here is racing the pump.
        root.Directory.Capacity.InUse.Should().Be(
            1, "the cancelled spawn's root-wide lease must come back, not stay charged forever");

        var entry = root.Directory.Resolve("second").Entry;
        entry.Should().NotBeNull(
            because: "the row is retained for correlation, not deleted");
        entry!.Status.Should().Be(
            AgentCollaborationStatuses.Stopped,
            because: "left as \"queued\" it would look like pending work that will eventually run");
        entry.IsLive.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_WithSpawnsStillQueued_ReclaimsRootCapacityAndRetiresEveryRow()
    {
        // Companion to the cancellation test above, exercising the OTHER pre-start exit: spawns still
        // sitting in the defer-queue when the MANAGER ITSELF is disposed (server shutdown / conversation
        // eviction), not when an individual caller cancels. Two queued spawns are used deliberately
        // (rather than one) so the disposal path drains at least one entry through EACH of the two
        // shutdown drain loops in SubAgentManager (the spawn pump's own tail drain in
        // RunSpawnPumpAsync, and DisposeAsync's own final _spawnQueue drain) — both must retire the
        // collaboration admission they took at queue time, not just fault the caller's StateReady.
        // Left unreclaimed, disposing a manager with queued work permanently shrinks the collaboration's
        // root-wide capacity by one agent per abandoned queued spawn.
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxTotalAgents = 5 });
        var (manager, _) = CreateManager(
            root,
            template: BlockingTemplate(),
            configure: options => options with { MaxConcurrentSubAgents = 1 });

        // Occupies the only local slot forever (BlockingTemplate never completes).
        _ = await manager.SpawnAsync(
            "worker", "work", name: "first", role: "worker role",
            description: "Holds the only local slot.", runInBackground: true);

        // Both queue behind the saturated local gate; neither ever gets a permit before disposal.
        _ = await manager.SpawnAsync(
            "worker", "work", name: "second", role: "worker role",
            description: "Queued behind the saturated gate.", runInBackground: true);
        _ = await manager.SpawnAsync(
            "worker", "work", name: "third", role: "worker role",
            description: "Also queued behind the saturated gate.", runInBackground: true);

        root.Directory.Capacity.InUse.Should().Be(
            3, "all three spawns admitted to the collaboration before any of them ran or queued");

        await manager.DisposeAsync();

        root.Directory.Capacity.InUse.Should().Be(
            0, "disposal must give back every root-wide lease — the one held by the running agent AND "
                + "the ones held by spawns that never got past the defer-queue");

        foreach (var name in new[] { "second", "third" })
        {
            var entry = root.Directory.Resolve(name).Entry;
            entry.Should().NotBeNull(because: "the row is retained for correlation, not deleted");
            entry!.Status.Should().Be(
                AgentCollaborationStatuses.Stopped,
                because: $"'{name}' never ran and must not be left looking like pending work");
            entry.IsLive.Should().BeFalse();
        }
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

    [Fact]
    public async Task AChildAtTheDelegationLimit_CanStillMessageItsPeers()
    {
        // Delegation and messaging are separate budgets: SubAgentToolProvider withholds only the SPAWN
        // tools at the depth limit and deliberately still offers GetAgents/SendMessage. That branch was
        // unreachable, though, because a child with no delegation budget was handed no sub-agent options
        // at all, and the loop builds its tool provider only when it has them. The result was a leaf
        // registered in the directory, holding an inbox and a write endpoint, addressable by every other
        // agent — and unable to say anything back. With the DEFAULT MaxDelegationDepth of 1 that is every
        // sub-agent there is, so collaboration messaging was effectively dead out of the box.
        //
        // Every other test here builds the child's manager by hand, which silently supplies what the real
        // spawn path did not. This one goes through the manager, which is the only way to see it.
        var root = CreateRegisteredRoot();
        var (peer, _) = RegisterPeer(root, "peer");
        var (manager, provider) = CreateManager(root, MessagingTemplate("peer"));

        var childId = await SpawnAndResolveIdAsync(provider);

        manager.GetChildCollaboration(childId)!.CanDelegate.Should().BeFalse(
            "the default limit makes the child a leaf — which is the case this test exists for");

        // Delivery is dispatched off the sender's turn, so this waits on the arrival itself rather than
        // on the child's completion — condition, not clock.
        var received = await peer.Received.WaitAsync(TimeSpan.FromSeconds(30));
        received.Body.Should().Be(LeafGreeting);
        received.FromName.Should().Be("child", "a leaf speaks for itself, not for its parent");
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

    [Fact]
    public async Task ADelegate_SpawnsAnOrdinaryChild_WithoutTheSpawnAuthorityHeldOverItsOwnSpawn()
    {
        // The three spawn hooks belong to ONE host at ONE level: a workflow controller closes them over
        // its live runtime so that ITS delegates match authored units and carry authored identity. Handed
        // down verbatim, they follow the delegate into its own delegations, where nothing is authored —
        // so an ordinary helper is rejected for not being a workflow unit, has its model choice replaced,
        // and is published in the directory wearing the controller's authored role. Every other test here
        // builds the deeper manager by hand, which never sees what the real spawn path passes down.
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxDelegationDepth = 2 });
        var (parentManager, parentProvider) = CreateManager(
            root,
            BlockingTemplate(),
            options => options with
            {
                AvailableModelIds = ["catalog-model"],
                SpawnNameGate = name =>
                    name == AuthoredUnit ? null : $"'{name}' is not a unit of this workflow.",
                SpawnModelSelectionResolver = _ => new SubAgentSpawnModelSelection("catalog-model", null),
                SpawnMetadataResolver = _ => new SubAgentSpawnMetadata(
                    "authored role", "Authored by the workflow, not by whoever called the tool."),
            });

        var delegateId = await SpawnAndResolveIdAsync(parentProvider, AuthoredUnit);
        var delegateLoop = ChildLoop(parentManager, delegateId);

        // A name no workflow unit could match, and metadata only the caller supplied: the two things the
        // inherited hooks would have overridden.
        var spawned = await InvokeAsync(delegateLoop.SubAgentTools!, "Agent", NewSpawn("ordinary-helper"));
        spawned.IsError.Should().BeFalse(spawned.Text);

        var helper = root.Directory.Snapshot()
            .Should().ContainSingle(e => e.Name == "ordinary-helper").Subject;

        helper.DelegationDepth.Should().Be(2);
        helper.ParentAgentId.Should().Be(delegateId);
        helper.Role.Should().Be("worker role", "a delegate's own helper is described by its delegate");
        helper.Description.Should().Be("Does a unit of work.");

        delegateLoop.SubAgentManager!.SpawnNameGate.Should().BeNull(
            "the gate names the units of the workflow above, not of the delegate's own work");
        delegateLoop.SubAgentManager.SpawnModelSelectionResolver.Should().BeNull(
            "authority over a spawn's model belongs to the host that authored that spawn");
        delegateLoop.SubAgentManager.AvailableModelIds.Should().Equal(
            ["catalog-model"], "the catalog is configuration, not authority, so it is inherited");
    }

    [Fact]
    public async Task APerAgentTool_IsBuiltFreshForEveryParticipant_AndNeverInheritedFromTheOneAbove()
    {
        // A tool that acts AS one agent (the #244 transcript read) cannot be shared or inherited: an
        // inherited instance hands a descendant its ancestor's reach. Registering it only on the root
        // instead is the mirror failure — the deeper ancestors, which are exactly who the feature exists
        // for, then have no way to read the children they spawned. The factory resolves both: one
        // instance per participant, bound to that participant.
        var boundTo = new List<string>();
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxDelegationDepth = 2 });
        var (parentManager, parentProvider) = CreateManager(
            root,
            BlockingTemplate(),
            options => options with
            {
                NonInheritedToolNames = [ViewerBoundProvider.ToolName],
                ChildToolProviderFactory = agentId =>
                {
                    boundTo.Add(agentId);
                    return new ViewerBoundProvider(agentId);
                },
            });

        var childId = await SpawnAndResolveIdAsync(parentProvider);
        var childLoop = ChildLoop(parentManager, childId);

        childLoop.RegisteredToolNames.Should().Contain(
            ViewerBoundProvider.ToolName, "a spawned participant gets its own instance of the tool");
        boundTo.Should().Equal([childId], "and that instance is bound to the child, not to its parent");

        childLoop.SubAgentManager!.GetInheritableToolSnapshot().Contracts.Select(c => c.Name)
            .Should().NotContain(
                ViewerBoundProvider.ToolName,
                "the child's own instance must not travel down to ITS children");

        var grandchildId = await SpawnAndResolveIdAsync(childLoop.SubAgentTools!, "grandchild");
        var grandchildLoop = ChildLoop(childLoop.SubAgentManager, grandchildId);

        boundTo.Should().Equal(
            [childId, grandchildId], "the factory travels down even though the instances do not");
        grandchildLoop.RegisteredToolNames.Should().ContainSingle(n => n == ViewerBoundProvider.ToolName);
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

    [Fact]
    public async Task GetAgents_ReportsBothDepthsAndWhetherATranscriptCanBeRead()
    {
        // One "depth" cannot mean two things. Structural depth is how deeply nested an agent is;
        // delegation depth is how much of its spawn budget is spent — and only the second says whether
        // it may spawn. Transcript readability is published for the same reason: a caller that has to
        // attempt a read to discover it may not read burns a turn on a refusal it could have foreseen.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);
        _ = await SpawnAndResolveIdAsync(provider, "peer");

        var payload = await InvokeAsync(provider, "GetAgents", new { });

        using var doc = JsonDocument.Parse(payload.Text);
        var child = doc.RootElement.GetProperty("agents").EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == "peer");

        child.GetProperty("structural_depth").GetInt32().Should().Be(1);
        child.GetProperty("delegation_depth").GetInt32().Should().Be(1);
        child.GetProperty("transcript_readable").GetBoolean().Should().BeTrue(
            because: "a parent may always read a child it spawned");
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
            msg_type = "question",
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
            msg_type = "question",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.UnknownTarget);
        payload.Text.Should().Contain("finished");
    }

    [Fact]
    public async Task SendMessage_FromARetiredSender_IsRefusedWithAnActionableToolMessage()
    {
        // AgentCollaborationBundleTests.TrySend_RefusesAnAgentThatHasLeft_AsTheSender covers this at
        // the bundle level. This is the same refusal one layer up, through the actual tool a retired
        // agent's own loop would still be holding: the provider built from ITS OWN
        // AgentCollaborationSetup, sending after something else (a parent cleanup, a lifecycle sweep)
        // retired it out from under it. The model behind that loop cannot see "invalid_sender" — only
        // the text — so the wording has to tell it what happened rather than leaving it to guess why a
        // message it just tried to send bounced.
        var root = CreateRegisteredRoot();
        var (_, peerSetup) = RegisterPeer(root, "peer");
        var (_, provider) = CreateManager(peerSetup);
        _ = root.Bundle.RetireAgent(peerSetup.AgentId, AgentCollaborationStatuses.Completed);

        var payload = await InvokeAsync(provider, "SendMessage", new
        {
            target = root.Name,
            content = "hello",
            msg_type = "question",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.InvalidSender);
        payload.Text.Should().Contain("no longer active");
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
            msg_type = "question",
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
    public async Task SendMessage_TaskUpdateWithoutCorrelation_IsRefusedBeforeAdmission()
    {
        // Progress on nothing is not progress. Admitted bare, it would reach the receiver with no way
        // to tell which delegation it was about.
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new
        {
            target = "helper",
            content = "half done",
            msg_type = "task_update",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be("missing_correlation");
    }

    [Fact]
    public async Task SendMessage_SteerToAnAgentThatIsNotRunning_IsRefusedAtAdmission()
    {
        // A steer redirects work in flight. Accepting one for an idle agent would either restart it —
        // the opposite of redirecting — or be dropped later, after the sender had been told "accepted".
        var root = CreateRegisteredRoot();
        var (_, peerSetup) = RegisterPeer(root, "helper");
        _ = root.Directory.TryUpdateStatus(
            peerSetup.AgentId, AgentCollaborationStatuses.Completed);
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new
        {
            target = "helper",
            content = "focus on the parser instead",
            msg_type = "steer",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.TargetNotActive);
    }

    [Fact]
    public async Task SendMessage_WhenTheSendersOwnTurnIsAlreadyCancelled_StillDelivers()
    {
        // Once admission returns "accepted" the collaboration has taken responsibility for the message.
        // The sender's tool-call token signals the end of the sender's turn, and letting that drop an
        // accepted message would leave the receiver waiting for something nobody is still carrying.
        var root = CreateRegisteredRoot();
        var (peerEndpoint, _) = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var payload = await InvokeAsync(provider, "SendMessage", new
        {
            target = "helper",
            content = "still needs to arrive",
            msg_type = "question",
        }, cts.Token);

        payload.IsError.Should().BeFalse(payload.Text);
        _ = await peerEndpoint.Received.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SendMessage_ToAFinishedChild_RestartsItAndSaysSoInTheDirectory()
    {
        // The directory is what every other agent reads. A child that has been woken but still reports
        // "completed" is unaddressable to a steer and invisible to anyone deciding whether to wait.
        var restart = new RestartCapturingTemplate();
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root, template: restart.Template);
        var childId = await SpawnAndResolveIdAsync(provider);
        _ = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = childId });

        root.Directory.FindById(childId)!.Status
            .Should().Be(AgentCollaborationStatuses.Completed);

        var dispatch = new AgentCollaborationMessenger(root).Send(
            childId, "One more thing", AgentMessageType.Question);
        await dispatch.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        root.Directory.FindById(childId)!.Status.Should().Be(AgentCollaborationStatuses.Running);
    }

    [Fact]
    public async Task AChildWhoseMonitorFaults_IsPublishedAsError_RatherThanLeftLookingLive()
    {
        // A monitor fault ends the run terminally. The directory is what every OTHER agent reads, so
        // leaving it at "running" keeps the whole hierarchy believing this child is live: a steer
        // addressed to it passes admission, and a completion barrier waits on a run that can never
        // answer. The fault is injected through the usage relay because that runs INSIDE the monitor
        // loop, before any completion handling — i.e. while the entry still says "running", which is
        // precisely the state under test.
        var sink = new Mock<IUsageSink>();
        _ = sink.Setup(s => s.RecordUsage(It.IsAny<UsageRecord>()))
            .Throws(new InvalidOperationException("usage sink unavailable"));

        _ = _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable([
                new UsageMessage
                {
                    Usage = new Usage { PromptTokens = 10, CompletionTokens = 5 },
                    GenerationId = "gen-1",
                },
                new TextMessage { Text = "never observed", Role = Role.Assistant },
            ])));

        var root = CreateRegisteredRoot();
        var (manager, provider) = CreateManager(root, usageSink: sink.Object);
        var childId = await SpawnAndResolveIdAsync(provider);

        // The fault resolves the completion latch, and it does so only AFTER the status publish below —
        // so awaiting it is a condition, not a clock.
        var observe = async () => await manager.ObserveCompletionAsync(childId, CancellationToken.None);
        _ = await observe.Should().ThrowAsync<InvalidOperationException>();

        root.Directory.FindById(childId)!.Status.Should().Be(AgentCollaborationStatuses.Error);
    }

    [Fact]
    public async Task SendMessage_ToARealSubAgent_ArrivesAsTheTypedAgentMessage()
    {
        // Flattening to text would strip the sender, the kind, and the correlation the UI and the
        // persisted history read back, leaving a rehydrated conversation unable to tell an
        // agent-to-agent message from anything a user might have typed.
        var restart = new RestartCapturingTemplate();
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root, template: restart.Template);
        var childId = await SpawnAndResolveIdAsync(provider);
        _ = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = childId });

        var dispatch = new AgentCollaborationMessenger(root).Send(
            childId, "Which branch did you use?", AgentMessageType.Question);
        dispatch.Result.Succeeded.Should().BeTrue();

        var seen = await restart.Restarted.WaitAsync(TimeSpan.FromSeconds(10));
        var relayed = seen.OfType<AgentMessage>().Should().ContainSingle().Subject;

        relayed.MessageId.Should().Be(dispatch.Result.MessageId);
        relayed.AgentMessageType.Should().Be(AgentMessageType.Question);
        relayed.FromAgentId.Should().Be(root.AgentId);
        relayed.Role.Should().Be(Role.User);
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

    // Regression coverage for a persisted bug: GetOptionalString already collapses an omitted key or an
    // explicit JSON null to C# null, but a JSON value that is a blank/whitespace STRING survived
    // unnormalized. For question/delegate_task/steer that skipped the Response/TaskUpdate-only missing-
    // correlation guard and was passed straight to the messenger, which the ledger then refused as
    // unknown_correlation (a non-null in_response_to that matches no admitted message) instead of
    // treating it as no correlation at all.

    [Theory]
    [InlineData("question")]
    [InlineData("delegate_task")]
    [InlineData("steer")]
    public async Task SendMessage_NonReplyType_WithOmittedCorrelation_Succeeds(string msgType)
    {
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new Dictionary<string, object?>
        {
            ["target"] = "helper",
            ["content"] = "hello",
            ["msg_type"] = msgType,
        });

        payload.IsError.Should().BeFalse(payload.Text);
    }

    [Theory]
    [InlineData("question")]
    [InlineData("delegate_task")]
    [InlineData("steer")]
    public async Task SendMessage_NonReplyType_WithJsonNullCorrelation_Succeeds(string msgType)
    {
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new Dictionary<string, object?>
        {
            ["target"] = "helper",
            ["content"] = "hello",
            ["msg_type"] = msgType,
            ["in_response_to"] = null,
        });

        payload.IsError.Should().BeFalse(payload.Text);
    }

    [Theory]
    [InlineData("question")]
    [InlineData("delegate_task")]
    [InlineData("steer")]
    public async Task SendMessage_NonReplyType_WithEmptyStringCorrelation_NormalizesToAbsentAndSucceeds(
        string msgType)
    {
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new Dictionary<string, object?>
        {
            ["target"] = "helper",
            ["content"] = "hello",
            ["msg_type"] = msgType,
            ["in_response_to"] = "",
        });

        payload.IsError.Should().BeFalse(payload.Text);
    }

    [Theory]
    [InlineData("question")]
    [InlineData("delegate_task")]
    [InlineData("steer")]
    public async Task SendMessage_NonReplyType_WithWhitespaceCorrelation_NormalizesToAbsentAndSucceeds(
        string msgType)
    {
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new Dictionary<string, object?>
        {
            ["target"] = "helper",
            ["content"] = "hello",
            ["msg_type"] = msgType,
            ["in_response_to"] = "   ",
        });

        payload.IsError.Should().BeFalse(payload.Text);
    }

    [Theory]
    [InlineData("response")]
    [InlineData("task_update")]
    public async Task SendMessage_ReplyType_WithJsonNullCorrelation_ReturnsMissingCorrelation(string msgType)
    {
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new Dictionary<string, object?>
        {
            ["target"] = "helper",
            ["content"] = "hello",
            ["msg_type"] = msgType,
            ["in_response_to"] = null,
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.MissingCorrelation);
    }

    [Theory]
    [InlineData("response")]
    [InlineData("task_update")]
    public async Task SendMessage_ReplyType_WithEmptyStringCorrelation_ReturnsMissingCorrelation(string msgType)
    {
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new Dictionary<string, object?>
        {
            ["target"] = "helper",
            ["content"] = "hello",
            ["msg_type"] = msgType,
            ["in_response_to"] = "",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.MissingCorrelation);
    }

    [Theory]
    [InlineData("response")]
    [InlineData("task_update")]
    public async Task SendMessage_ReplyType_WithWhitespaceCorrelation_ReturnsMissingCorrelation(string msgType)
    {
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new Dictionary<string, object?>
        {
            ["target"] = "helper",
            ["content"] = "hello",
            ["msg_type"] = msgType,
            ["in_response_to"] = "   ",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.MissingCorrelation);
    }

    [Fact]
    public async Task SendMessage_ResponseWithARealButUnknownCorrelation_IsRefusedAsUnknownCorrelation()
    {
        // Proves the blank-normalization fix is scoped to blank/whitespace only: a real, well-formed but
        // unrecognized id must still be refused rather than silently treated as absent.
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new
        {
            target = "helper",
            content = "the answer",
            msg_type = "response",
            in_response_to = "agentmsg-does-not-exist",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.UnknownCorrelation);
    }

    [Fact]
    public async Task SendMessage_QuestionWithARealButUnknownCorrelation_IsRefusedAsUnknownCorrelation()
    {
        // Same as above, for a NON-reply type: this is exactly the code path the fix touches, so it must
        // not collapse a real-but-wrong id to "no correlation" the way it does for blank ones.
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "SendMessage", new
        {
            target = "helper",
            content = "hello",
            msg_type = "question",
            in_response_to = "agentmsg-does-not-exist",
        });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.UnknownCorrelation);
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
    public async Task CheckAgents_ObservesAnAgentThatIsNotOneOfMyChildren()
    {
        // Collaboration made every agent addressable, so observation has to reach as far as addressing
        // does: being told "not found" about an agent you can message and are waiting on is a dead end.
        var root = CreateRegisteredRoot();
        var (_, peerSetup) = RegisterPeer(root, "cousin");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "CheckAgents", new
        {
            agent_ids = peerSetup.AgentId,
        });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("not_found").GetInt32().Should().Be(0);
        var observed = doc.RootElement.GetProperty("agents").EnumerateArray().Single();
        observed.GetProperty("name").GetString().Should().Be("cousin");
        observed.GetProperty("status").GetString().Should().Be(AgentCollaborationStatuses.Running);
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
    public async Task WaitForAgents_IsNotInterruptedTwiceByTheSameUnansweredQuestion()
    {
        // A question stays open until it is answered, so without a one-shot claim every later wait
        // would rediscover it in the sweep and return at once — an agent that chose not to answer would
        // spin instead of waiting, and could never wait for its children again.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root, template: BlockingTemplate());
        var agentId = await SpawnAndResolveIdAsync(provider);
        var (_, peerSetup) = RegisterPeer(root, "asker");

        var dispatch = new AgentCollaborationMessenger(peerSetup).Send(
            root.AgentId, "Which branch?", AgentMessageType.Question);
        await dispatch.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        var first = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId });
        using var firstDoc = JsonDocument.Parse(first.Text);
        firstDoc.RootElement.GetProperty("status").GetString().Should().Be("question_received");

        var second = await InvokeAsync(provider, "WaitForAgents", new
        {
            agent_ids = agentId,
            timeout_seconds = 1,
        });

        using var secondDoc = JsonDocument.Parse(second.Text);
        secondDoc.RootElement.GetProperty("status").GetString().Should().Be("timeout");

        // Interrupting is not answering: the question is still owed a reply.
        root.Bundle.Ledger.GetOpenInbound(root.AgentId).Should().ContainSingle();
    }

    [Fact]
    public async Task WaitForAgents_GivesBackAQuestionClaimThatLostToACompletion()
    {
        // The other half of the one-shot claim, and the half a passing suite hides: the claim is taken
        // by the sweep BEFORE the race is decided, so a wait that ends for a COMPLETION has already
        // spent the question's single interrupt without reporting it. Unless the claim is handed back,
        // that question — still open, still owed an answer — can never wake any later wait, and the
        // agent it was addressed to goes on waiting for its remaining children with no way to notice
        // it was asked. Both waits below are decided by what is already complete when the race is
        // built, so the ordering is structural rather than a timing bet.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(
            root,
            configure: options => options with
            {
                Templates = new Dictionary<string, SubAgentTemplate>(options.Templates)
                {
                    ["blocker"] = BlockingTemplate(),
                },
            });

        var finishedId = await SpawnAndResolveIdAsync(provider, "finished");
        var blockedId = await SpawnAndResolveIdAsync(provider, "still-running", "blocker");

        // Settle the finished child while no question exists, so the wait that loses below is racing an
        // observation that was already complete before the race began.
        using (var settled = JsonDocument.Parse(
            (await InvokeAsync(provider, "WaitForAgents", new { agent_ids = finishedId })).Text))
        {
            settled.RootElement.GetProperty("status").GetString().Should().Be("completed");
        }

        var (_, peerSetup) = RegisterPeer(root, "asker");
        var dispatch = new AgentCollaborationMessenger(peerSetup).Send(
            root.AgentId, "Which branch?", AgentMessageType.Question);
        await dispatch.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        var lost = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = finishedId });
        using var lostDoc = JsonDocument.Parse(lost.Text);
        lostDoc.RootElement.GetProperty("status").GetString().Should().Be("completed");
        lostDoc.RootElement.GetProperty("question").ValueKind.Should().Be(
            JsonValueKind.Null, "the completion won, so no question is being reported to the caller");

        // The next wait has nothing to complete, so only the question can end it — which it can only do
        // if the claim the previous wait took but never used was returned.
        var interrupted = await InvokeAsync(provider, "WaitForAgents", new
        {
            agent_ids = blockedId,
            timeout_seconds = 5,
        });

        using var interruptedDoc = JsonDocument.Parse(interrupted.Text);
        interruptedDoc.RootElement.GetProperty("status").GetString()
            .Should().Be("question_received");
        interruptedDoc.RootElement.GetProperty("question").GetProperty("from_name").GetString()
            .Should().Be("asker");

        root.Bundle.Ledger.GetOpenInbound(root.AgentId).Should().ContainSingle(
            "being interrupted by a question is still not an answer to it");
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

    /// <summary>The one spawn name a workflow-style <c>SpawnNameGate</c> in these tests will allow.</summary>
    private const string AuthoredUnit = "authored-unit";

    private static object NewSpawn(string name, string subagentType = "worker") => new
    {
        subagent_type = subagentType,
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
        SubAgentTemplate? template = null,
        Func<SubAgentOptions, SubAgentOptions>? configure = null,
        IUsageSink? usageSink = null)
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

        options = configure?.Invoke(options) ?? options;

        var source = new MutableSubAgentTemplateSource(options.Templates);
        var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: source,
            usageSink: usageSink,
            collaboration: collaboration);

        _managers.Add(manager);
        return (manager, new SubAgentToolProvider(manager, source));
    }

    /// <summary>A template whose agent cannot be built, standing in for a bad model or a dead provider.</summary>
    private static SubAgentTemplate FailingTemplate() => new()
    {
        SystemPrompt = "You are a worker.",
        AgentFactory = () => throw new InvalidOperationException("provider unavailable"),
    };

    private const string LeafGreeting = "Reporting in from the depth limit.";

    /// <summary>
    /// A worker whose first turn messages <paramref name="target"/> and whose second ends the run. The
    /// tool call goes through the child's OWN loop, so it can only succeed if that loop was given the
    /// collaboration tool surface — which is the point of the test that uses this.
    /// </summary>
    private static SubAgentTemplate MessagingTemplate(string target) => new()
    {
        SystemPrompt = "You are a worker.",
        AgentFactory = () =>
        {
            var turn = 0;
            var mock = new Mock<IStreamingAgent>();
            _ = mock
                .Setup(a => a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(ToAsyncEnumerable(
                    Interlocked.Increment(ref turn) == 1
                        ? [new ToolCallMessage
                            {
                                FunctionName = "SendMessage",
                                FunctionArgs = JsonSerializer.Serialize(new
                                {
                                    target,
                                    content = LeafGreeting,
                                    // A question stands on its own. The reply-only types (response,
                                    // task_update) are refused without an in_response_to, and this
                                    // template has nothing to correlate to.
                                    msg_type = "question",
                                }),
                                ToolCallId = "tc_1",
                                Role = Role.Assistant,
                            }]
                        : [new TextMessage { Text = "done", Role = Role.Assistant }])));
            return mock.Object;
        },
    };

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
        string name = "child",
        string subagentType = "worker")
    {
        var payload = await InvokeAsync(provider, "Agent", NewSpawn(name, subagentType));
        payload.IsError.Should().BeFalse(payload.Text);

        using var doc = JsonDocument.Parse(payload.Text);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    /// <summary>
    /// The loop the REAL spawn path built for <paramref name="agentId"/> — the only place the options and
    /// tools a child actually runs on can be observed.
    /// </summary>
    private static MultiTurnAgentLoop ChildLoop(SubAgentManager manager, string agentId)
    {
        manager.TryGetAgent(agentId, out var agent).Should().BeTrue();
        return agent.Should().BeOfType<MultiTurnAgentLoop>().Subject;
    }

    /// <summary>
    /// A stand-in for a tool that acts AS one agent (the sample's transcript reader): it carries the id it
    /// was built for, so a test can tell "each participant got its own" from "one instance was shared".
    /// </summary>
    private sealed class ViewerBoundProvider(string viewerAgentId) : IFunctionProvider
    {
        public const string ToolName = "ReadAsViewer";

        public string ProviderName => "ViewerBoundTools";

        public int Priority => 100;

        public IEnumerable<FunctionDescriptor> GetFunctions() =>
        [
            new FunctionDescriptor
            {
                Contract = new FunctionContract
                {
                    Name = ToolName,
                    Description = "Acts as exactly one agent.",
                },
                Handler = (_, _, _) =>
                    Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(viewerAgentId)),
                ProviderName = "ViewerBoundTools",
            },
        ];
    }

    private static async Task<ToolHandlerResultPayload> InvokeAsync(
        SubAgentToolProvider provider,
        string toolName,
        object args,
        CancellationToken ct = default)
    {
        var handler = provider.GetFunctions().First(f => f.Contract.Name == toolName).Handler;
        var result = await handler(
            JsonSerializer.Serialize(args), new ToolCallContext(), ct);

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

    /// <summary>
    /// A sub-agent whose first run finishes immediately and whose second run captures what it was
    /// handed and then blocks.
    /// </summary>
    /// <remarks>
    /// The second run is the restart a collaboration message triggers, and it is the only point at
    /// which the delivered message can be seen as the child itself sees it. Blocking there keeps the
    /// child in <c>running</c> for the assertions rather than racing them to completion.
    /// </remarks>
    private sealed class RestartCapturingTemplate
    {
        private readonly TaskCompletionSource<IReadOnlyList<IMessage>> _restarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _runs;

        public RestartCapturingTemplate()
        {
            Template = new SubAgentTemplate
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
                            (messages, _, ct) => Task.FromResult(Run(messages, ct)));
                    return mock.Object;
                },
            };
        }

        /// <summary>What the restarted run was given, once it has begun.</summary>
        public Task<IReadOnlyList<IMessage>> Restarted => _restarted.Task;

        public SubAgentTemplate Template { get; }

        private IAsyncEnumerable<IMessage> Run(
            IEnumerable<IMessage> messages,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _runs) == 1)
            {
                return ToAsyncEnumerable(
                    [new TextMessage { Text = "done", Role = Role.Assistant }]);
            }

            _ = _restarted.TrySetResult([.. messages]);
            return BlockingStream(ct);
        }
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
