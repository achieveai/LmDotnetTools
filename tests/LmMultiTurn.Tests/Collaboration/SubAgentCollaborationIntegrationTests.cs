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
using AchieveAi.LmDotnetTools.LmTestUtils;
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
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        SetupSubAgentReply("done");
        return Task.CompletedTask;
    }

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

    #region Tool surface

    [Fact]
    public void GetFunctions_WithoutCollaboration_KeepsLegacySurfaceExactly()
    {
        var (_, provider) = CreateManager(collaboration: null);

        var functions = provider.GetFunctions().ToList();

        functions
            .Select(f => f.Contract.Name)
            .Should()
            .BeEquivalentTo(["Agent", "SendMessage", "CheckAgent", "WaitAgent"]);

        var agentParams = functions.First(f => f.Contract.Name == "Agent").Contract.Parameters!;
        agentParams.Select(p => p.Name).Should().NotContain("role");
        agentParams.First(p => p.Name == "description").IsRequired.Should().BeFalse();

        functions
            .First(f => f.Contract.Name == "SendMessage")
            .Contract.Parameters!.Select(p => p.Name)
            .Should()
            .BeEquivalentTo(["target", "prompt", "run_in_background", "idempotency_key"]);
    }

    [Fact]
    public void GetFunctions_WithCollaboration_SwapsInTheCollaborationSurface()
    {
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var names = provider.GetFunctions().Select(f => f.Contract.Name).ToList();

        names.Should().BeEquivalentTo(["Agent", "CheckAgents", "WaitForAgents", "GetAgents", "SendMessage"]);
        names.Should().NotContain("CheckAgent");
        names
            .Should()
            .NotContain(
                "WaitAgent",
                "the singular legacy wait is replaced by WaitForAgents — no alias is added under collaboration"
            );
    }

    [Fact]
    public void GetFunctions_AtDelegationLimit_HidesOnlySpawningAndKeepsCollaboration()
    {
        // Depth 0 means "this collaboration exists, but nobody may spawn". Only Agent goes: an agent
        // that cannot delegate must still find, message, observe, and wait on the agents that already
        // exist, and hiding CheckAgents or WaitForAgents would leave it able to ask a question it
        // could never notice the answer to.
        var (_, provider) = CreateManager(
            CreateRegisteredRoot(new AgentCollaborationOptions { MaxDelegationDepth = 0 })
        );

        provider
            .GetFunctions()
            .Select(f => f.Contract.Name)
            .Should()
            .BeEquivalentTo(["CheckAgents", "WaitForAgents", "GetAgents", "SendMessage"]);
    }

    [Fact]
    public void AgentDescriptor_WithCollaboration_RequiresDirectoryMetadata()
    {
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var parameters = provider.GetFunctions().First(f => f.Contract.Name == "Agent").Contract.Parameters!;

        parameters.Select(p => p.Name).Should().Contain("role");
        parameters.First(p => p.Name == "description").IsRequired.Should().BeTrue();
    }

    [Fact]
    public void AgentDescriptor_WarnsThatRoleAndDescriptionAreVisibleToEveryone()
    {
        // Both fields are published into a directory every agent can read, so the model has to be told
        // before it writes them — a warning added afterwards cannot un-share what was already put there.
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var parameters = provider.GetFunctions().First(f => f.Contract.Name == "Agent").Contract.Parameters!;

        foreach (var name in new[] { "role", "description" })
        {
            parameters.First(p => p.Name == name).Description.Should().Contain("secrets").And.Contain("customer data");
        }
    }

    [Fact]
    public void SendMessageDescriptor_ConstrainsMsgTypeToTheKindsThatExist()
    {
        // An open string invites a kind the handler will only reject after the fact. The enum spends
        // the model's mistake at schema time, where it costs nothing.
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var msgType = provider
            .GetFunctions()
            .First(f => f.Contract.Name == "SendMessage")
            .Contract.Parameters!.First(p => p.Name == "msg_type");

        msgType
            .ParameterType!.Enum.Should()
            .BeEquivalentTo(["question", "delegate_task", "task_update", "steer", "response"]);
    }

    [Fact]
    public void WaitForAgentsDescriptor_SaysBothWhenToWaitAndWhenNotTo()
    {
        // Waiting is the one collaboration tool that costs the caller its turn, so the description has
        // to rule cases out as explicitly as it rules them in.
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var description = provider.GetFunctions().First(f => f.Contract.Name == "WaitForAgents").Contract.Description!;

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
        waitForAgents.Should().Contain("Use `agent_ids` returned by `Agent`").And.Contain("do not pass workflow IDs.");

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
            }
        );

        var payload = await InvokeAsync(
            provider,
            "Agent",
            new
            {
                subagent_type = "worker",
                prompt = "review",
                role = "release manager",
                description = "Reviews the auth change.",
                run_in_background = true,
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(SubAgentCollaborationFailureCodes.InvalidRole);
    }

    [Fact]
    public async Task Spawn_WithoutRole_IsRefusedWithAnActionableCode()
    {
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var payload = await InvokeAsync(
            provider,
            "Agent",
            new
            {
                subagent_type = "worker",
                prompt = "work",
                description = "Handles the migration.",
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(SubAgentCollaborationFailureCodes.InvalidRole);
    }

    [Fact]
    public async Task Spawn_WithoutDescription_IsRefusedWithAnActionableCode()
    {
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var payload = await InvokeAsync(
            provider,
            "Agent",
            new
            {
                subagent_type = "worker",
                prompt = "work",
                role = "migrator",
            }
        );

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
            }
        );

        var payload = await InvokeAsync(
            provider,
            "Agent",
            new
            {
                subagent_type = "worker",
                prompt = "review",
                description = "Reviews the auth change.",
                run_in_background = true,
            }
        );

        payload.IsError.Should().BeFalse();
        manager.Collaboration!.Directory.Snapshot().Should().ContainSingle(e => e.Role == "code reviewer");
    }

    [Fact]
    public async Task Spawn_PublishesTheChildIntoTheSharedDirectory()
    {
        var root = CreateRegisteredRoot();
        var (manager, provider) = CreateManager(root);

        _ = await InvokeAsync(
            provider,
            "Agent",
            new
            {
                subagent_type = "worker",
                prompt = "work",
                role = "migrator",
                description = "Owns the auth migration.",
                name = "auth-migrator",
                run_in_background = true,
            }
        );

        var child = manager
            .Collaboration!.Directory.Snapshot()
            .Should()
            .ContainSingle(e => e.Name == "auth-migrator")
            .Subject;

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
        root.Directory.FindById(root.AgentId)
            .Should()
            .NotBeNull(because: "retiring a child must not disturb the agent that spawned it");
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
        root.Directory.Capacity.InUse.Should().Be(0, "a spawn that never produced an agent is not occupying a slot");
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
            configure: options => options with { MaxConcurrentSubAgents = 1 }
        );

        // Occupies the only local slot forever (BlockingTemplate never completes), so the gate the
        // second spawn queues behind can never free up on its own.
        _ = await manager.SpawnAsync(
            "worker",
            "work",
            name: "first",
            role: "worker role",
            description: "Holds the only local slot.",
            runInBackground: true
        );

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
            ct: cts.Token
        );

        root.Directory.Capacity.InUse.Should()
            .Be(2, "the queued spawn already holds a root-wide lease even though it never got a local slot");
        root.Directory.Resolve("second").Entry!.Status.Should().Be(AgentCollaborationStatuses.Queued);

        cts.Cancel();
        var act = async () => await queuedSpawn;
        _ = await act.Should().ThrowAsync<OperationCanceledException>();

        // Observing the cancellation does NOT imply the reclaim has run. The caller is released by
        // `await queued.StateReady.Task.WaitAsync(ct)` (SubAgentManager.cs:631), and WaitAsync(ct)
        // throws the instant the CALLER's token fires — it does not wait on the pump. The reclaim
        // lives in CancelQueuedSpawn, which the pump reaches on a separately scheduled continuation
        // off its own gate-cancellation path. So the two are genuinely concurrent, and an earlier
        // version of this test asserted immediately here and flaked under solution-wide parallel
        // load (never alone), because the assertion could win the race against the pump.
        //
        // Wait for the reclaim rather than assuming an ordering that does not exist. This does not
        // weaken the test: a reclaim that never happens now fails HERE, with a named timeout, instead
        // of at the assertion below — it just no longer fails a reclaim that happened a few
        // microseconds late. CancelQueuedSpawn retires before it cancels the caller, so capacity
        // reaching 1 also implies the directory row has been retired.
        await Wait.UntilAsync(
            () => root.Directory.Capacity.InUse == 1,
            "the cancelled spawn's root-wide lease came back",
            TimeSpan.FromSeconds(5),
            observed: () => $"root.Directory.Capacity.InUse={root.Directory.Capacity.InUse}"
        );

        root.Directory.Capacity.InUse.Should()
            .Be(1, "the cancelled spawn's root-wide lease must come back, not stay charged forever");

        var entry = root.Directory.Resolve("second").Entry;
        entry.Should().NotBeNull(because: "the row is retained for correlation, not deleted");
        entry!
            .Status.Should()
            .Be(
                AgentCollaborationStatuses.Stopped,
                because: "left as \"queued\" it would look like pending work that will eventually run"
            );
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
            configure: options => options with { MaxConcurrentSubAgents = 1 }
        );

        // Occupies the only local slot forever (BlockingTemplate never completes).
        _ = await manager.SpawnAsync(
            "worker",
            "work",
            name: "first",
            role: "worker role",
            description: "Holds the only local slot.",
            runInBackground: true
        );

        // Both queue behind the saturated local gate; neither ever gets a permit before disposal.
        _ = await manager.SpawnAsync(
            "worker",
            "work",
            name: "second",
            role: "worker role",
            description: "Queued behind the saturated gate.",
            runInBackground: true
        );
        _ = await manager.SpawnAsync(
            "worker",
            "work",
            name: "third",
            role: "worker role",
            description: "Also queued behind the saturated gate.",
            runInBackground: true
        );

        root.Directory.Capacity.InUse.Should()
            .Be(3, "all three spawns admitted to the collaboration before any of them ran or queued");

        await manager.DisposeAsync();

        root.Directory.Capacity.InUse.Should()
            .Be(
                0,
                "disposal must give back every root-wide lease — the one held by the running agent AND "
                    + "the ones held by spawns that never got past the defer-queue"
            );

        foreach (var name in new[] { "second", "third" })
        {
            var entry = root.Directory.Resolve(name).Entry;
            entry.Should().NotBeNull(because: "the row is retained for correlation, not deleted");
            entry!
                .Status.Should()
                .Be(
                    AgentCollaborationStatuses.Stopped,
                    because: $"'{name}' never ran and must not be left looking like pending work"
                );
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

        var act = () =>
            grandchildManager.SpawnAsync("worker", "work", role: "deeper", description: "Should never exist.");

        (await act.Should().ThrowAsync<SubAgentCollaborationException>())
            .Which.FailureCode.Should()
            .Be(SubAgentCollaborationFailureCodes.DepthLimit);
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

        manager
            .GetChildCollaboration(childId)!
            .CanDelegate.Should()
            .BeFalse("the default limit makes the child a leaf — which is the case this test exists for");

        // Delivery is dispatched off the sender's turn, so this waits on the arrival itself rather than
        // on the child's completion — condition, not clock.
        var received = await peer.Received.WaitAsync(TimeSpan.FromSeconds(30));
        received.Body.Should().Be(LeafGreeting);
        received.FromName.Should().Be("child", "a leaf speaks for itself, not for its parent");
    }

    /// <summary>
    /// Hiding <c>Agent</c> is guidance for the NEXT turn; history is not rewritten, so a model that
    /// saw the tool before the limit was reached can still call it. That call used to come back as a
    /// bare "Unknown function" — the same answer a hallucinated tool name gets — which told the agent
    /// neither that it had hit a rule nor what to do instead. #671: the loop asks the provider why the
    /// tool is gone and returns that, with the code carried on the result rather than only in prose.
    /// </summary>
    [Fact]
    public async Task AgentCall_AtDepthLimit_ReturnsDepthLimitTeachingRefusal_NotUnknownFunction()
    {
        // MaxDelegationDepth 0: the collaboration exists and nobody may spawn, so EmitShape withholds
        // Agent and the loop never registers its handler.
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxDelegationDepth = 0 });
        var result = await DriveOneToolCallAsync(root, "Agent");

        result.IsError.Should().BeTrue("a withdrawn tool did not run — the refusal must not read as a success");
        result.ErrorCode.Should().Be(SubAgentCollaborationFailureCodes.DepthLimit);

        using var doc = JsonDocument.Parse(result.Result);
        doc.RootElement.GetProperty("code").GetString().Should().Be(SubAgentCollaborationFailureCodes.DepthLimit);
        doc.RootElement.GetProperty("error").GetString().Should().NotContain("Unknown function");
        doc.RootElement.GetProperty("error")
            .GetString()
            .Should()
            .Contain("SendMessage", "a refusal without a next valid action leaves the obligation stranded");
        doc.RootElement.GetProperty("available_functions")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .Contain("GetAgents");
    }

    /// <summary>
    /// The discriminator. SAME loop, SAME harness, one variable changed — a name nothing ever
    /// advertised — so the teaching refusal above is evidence about withdrawal, not about every
    /// unregistered name.
    /// </summary>
    [Fact]
    public async Task AnInventedToolName_AtDepthLimit_StillGetsThePlainUnknownFunctionError()
    {
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxDelegationDepth = 0 });
        var result = await DriveOneToolCallAsync(root, "TotallyMadeUp");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().BeNull();

        using var doc = JsonDocument.Parse(result.Result);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("Unknown function");
        doc.RootElement.TryGetProperty("code", out _).Should().BeFalse();
    }

    /// <summary>
    /// Both withdrawal reasons can hold at once, and they are not interchangeable: suppression lifts
    /// at the end of the turn, the depth limit never does. Reporting the temporary one here would
    /// promise a retry that is guaranteed to fail forever, so the PERMANENT reason wins.
    /// </summary>
    [Fact]
    public async Task AgentCall_SuppressedAndAtTheDepthLimit_ReportsTheDepthLimit_NotAThisTurnPause()
    {
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxDelegationDepth = 0 });
        var result = await DriveOneToolCallAsync(root, "Agent", suppressSpawning: true);

        result.ErrorCode.Should().Be(SubAgentCollaborationFailureCodes.DepthLimit);

        using var doc = JsonDocument.Parse(result.Result);
        doc.RootElement.GetProperty("error")
            .GetString()
            .Should()
            .NotContain("for this turn", "waiting out the turn cannot clear a limit that outlives it");
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

        _ = await InvokeAsync(
            childProvider,
            "Agent",
            new
            {
                subagent_type = "worker",
                prompt = "deep work",
                role = "specialist",
                description = "Does the deepest piece.",
                name = "specialist",
                run_in_background = true,
            }
        );

        var grandchild = root.Directory.Snapshot().Should().ContainSingle(e => e.Name == "specialist").Subject;

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
            options =>
                options with
                {
                    AvailableModelIds = ["catalog-model"],
                    SpawnNameGate = name => name == AuthoredUnit ? null : $"'{name}' is not a unit of this workflow.",
                    SpawnModelSelectionResolver = _ => new SubAgentSpawnModelSelection("catalog-model", null),
                    SpawnTypeModelSelectionResolver = _ => new SubAgentSpawnModelSelection(null, 3),
                    SpawnMetadataResolver = _ => new SubAgentSpawnMetadata(
                        "authored role",
                        "Authored by the workflow, not by whoever called the tool."
                    ),
                }
        );

        var delegateId = await SpawnAndResolveIdAsync(parentProvider, AuthoredUnit);
        var delegateLoop = ChildLoop(parentManager, delegateId);

        // A name no workflow unit could match, and metadata only the caller supplied: the two things the
        // inherited hooks would have overridden.
        var spawned = await InvokeAsync(delegateLoop.SubAgentTools!, "Agent", NewSpawn("ordinary-helper"));
        spawned.IsError.Should().BeFalse(spawned.Text);

        var helper = root.Directory.Snapshot().Should().ContainSingle(e => e.Name == "ordinary-helper").Subject;

        helper.DelegationDepth.Should().Be(2);
        helper.ParentAgentId.Should().Be(delegateId);
        helper.Role.Should().Be("worker role", "a delegate's own helper is described by its delegate");
        helper.Description.Should().Be("Does a unit of work.");

        delegateLoop
            .SubAgentManager!.SpawnNameGate.Should()
            .BeNull("the gate names the units of the workflow above, not of the delegate's own work");
        delegateLoop
            .SubAgentManager.SpawnModelSelectionResolver.Should()
            .BeNull("authority over a spawn's model belongs to the host that authored that spawn");
        delegateLoop
            .SubAgentManager.SpawnTypeModelSelectionResolver.Should()
            .NotBeNull("a mode-wide type policy applies at every review delegation depth");
        delegateLoop
            .SubAgentManager.AvailableModelIds.Should()
            .Equal(["catalog-model"], "the catalog is configuration, not authority, so it is inherited");
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
            options =>
                options with
                {
                    NonInheritedToolNames = [ViewerBoundProvider.ToolName],
                    ChildToolProviderFactory = agentId =>
                    {
                        boundTo.Add(agentId);
                        return new ViewerBoundProvider(agentId);
                    },
                }
        );

        var childId = await SpawnAndResolveIdAsync(parentProvider);
        var childLoop = ChildLoop(parentManager, childId);

        childLoop
            .RegisteredToolNames.Should()
            .Contain(ViewerBoundProvider.ToolName, "a spawned participant gets its own instance of the tool");
        boundTo.Should().Equal([childId], "and that instance is bound to the child, not to its parent");

        childLoop
            .SubAgentManager!.GetInheritableToolSnapshot()
            .Contracts.Select(c => c.Name)
            .Should()
            .NotContain(ViewerBoundProvider.ToolName, "the child's own instance must not travel down to ITS children");

        var grandchildId = await SpawnAndResolveIdAsync(childLoop.SubAgentTools!, "grandchild");
        var grandchildLoop = ChildLoop(childLoop.SubAgentManager, grandchildId);

        boundTo.Should().Equal([childId, grandchildId], "the factory travels down even though the instances do not");
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
        var child = doc
            .RootElement.GetProperty("agents")
            .EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == "peer");

        child.GetProperty("structural_depth").GetInt32().Should().Be(1);
        child.GetProperty("delegation_depth").GetInt32().Should().Be(1);
        child
            .GetProperty("transcript_readable")
            .GetBoolean()
            .Should()
            .BeTrue(because: "a parent may always read a child it spawned");
    }

    [Fact]
    public async Task GetAgents_WithinTheCap_ReportsTheCountsAndNoTruncation()
    {
        // The counts are unconditional so a reader never has to infer completeness from the array
        // length: "returned == total, truncated false" is the statement that the listing is whole.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);
        _ = RegisterPeer(root, "helper");

        var payload = await InvokeAsync(provider, "GetAgents", new { });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("returned").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        doc.RootElement.TryGetProperty("truncation_note", out _)
            .Should()
            .BeFalse("an untruncated listing pays nothing for a cap that did not bite");
    }

    [Fact]
    public async Task GetAgents_OverTheCap_DropsRetainedAgentsAndAnnouncesTheTruncation()
    {
        // A retired agent's row is never removed from the directory, so the listing grows without
        // bound over a long run and every turn pays for the whole history in input tokens. The cap
        // trims that tail — and says so, because a silently truncated directory invites the model to
        // conclude an agent it cannot see does not exist.
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxTotalAgents = 3 });
        var (_, provider) = CreateManager(root);
        _ = RegisterPeer(root, "live-a");
        _ = RegisterPeer(root, "live-b");
        foreach (var name in new[] { "done-a", "done-b", "done-c" })
        {
            var (_, peer) = RegisterPeer(root, name);
            _ = root.Bundle.RetireAgent(peer.AgentId, AgentCollaborationStatuses.Completed);
        }

        var payload = await InvokeAsync(provider, "GetAgents", new { });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(6);
        doc.RootElement.GetProperty("returned").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("truncation_note").GetString().Should().Contain("finished");

        var listed = doc.RootElement.GetProperty("agents").EnumerateArray().ToList();
        listed.Should().OnlyContain(a => a.GetProperty("is_live").GetBoolean());
        listed.Select(a => a.GetProperty("name").GetString()).Should().BeEquivalentTo([root.Name, "live-a", "live-b"]);
    }

    [Fact]
    public async Task GetAgents_WithMoreLiveAgentsThanTheCap_StillListsEveryLiveAgent()
    {
        // The cap bounds the RETAINED tail only. Hiding a live agent would be the one failure mode
        // this change must not have: the caller would spawn a duplicate of an agent it can already
        // address, which costs far more than the tokens the cap saves.
        var root = CreateRegisteredRoot(new AgentCollaborationOptions { MaxTotalAgents = 1 });
        var (_, provider) = CreateManager(root);
        _ = RegisterPeer(root, "live-a");
        _ = RegisterPeer(root, "live-b");
        var (_, retired) = RegisterPeer(root, "done-a");
        _ = root.Bundle.RetireAgent(retired.AgentId, AgentCollaborationStatuses.Completed);

        var payload = await InvokeAsync(provider, "GetAgents", new { });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("returned").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(4);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("agents")
            .EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .Should()
            .BeEquivalentTo([root.Name, "live-a", "live-b"]);
    }

    #endregion

    #region SendMessage

    [Fact]
    public async Task SendMessage_UnderCollaboration_AdmitsAndDeliversToAnyRegisteredAgent()
    {
        var root = CreateRegisteredRoot();
        var (peerEndpoint, _) = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "What does the auth flag default to?",
                msg_type = "question",
            }
        );

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

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "nobody",
                content = "hello",
                msg_type = "question",
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentDirectoryFailureCodes.NotFound);
        payload.Text.Should().Contain("GetAgents");
    }

    [Fact]
    public async Task SendMessage_ToAnAgentLostToARestart_SaysToSpawnItAgain()
    {
        // The third of the three ways to be unreachable, and the only one whose recovery is "make a new
        // one". A wrong name wants correcting and a finished agent wants leaving alone; an agent this
        // process never had wants replacing, and the model cannot work that out from "no agent matches".
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);
        AgentCollaborationRestartReconciler
            .Reconcile(
                root.Bundle,
                new AgentIdentityBindingSet
                {
                    CollaborationId = root.Bundle.CollaborationId,
                    RootAgentId = root.AgentId,
                    CapturedAtUtc = DateTimeOffset.UnixEpoch,
                    Agents =
                    [
                        new CollaborationNodeRecord
                        {
                            AgentId = "agent-99",
                            CollaborationId = root.Bundle.CollaborationId,
                            Name = "helper",
                            ParentAgentId = root.AgentId,
                            AncestorAgentIds = [root.AgentId],
                            Kind = AgentKind.SubAgent,
                            Role = "helper",
                            Description = "helps",
                            StructuralDepth = 1,
                            DelegationDepth = 1,
                            Status = AgentCollaborationStatuses.Running,
                        },
                    ],
                }
            )
            .Invalidated.Should()
            .ContainSingle("the refusal below is worthless if nothing was actually tombstoned");

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "hello",
                msg_type = "question",
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentDirectoryFailureCodes.TargetNotLive);
        payload.Text.Should().Contain("restarted").And.Contain("Agent").And.Contain("GetAgents");
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

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "hello",
                msg_type = "question",
            }
        );

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

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = root.Name,
                content = "hello",
                msg_type = "question",
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.InvalidSender);
        payload.Text.Should().Contain("no longer active");
    }

    [Fact]
    public async Task SendMessage_ToSelf_IsRefused()
    {
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = root.AgentId,
                content = "hello",
                msg_type = "question",
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.SelfDelivery);
    }

    [Fact]
    public async Task SendMessage_ResponseWithoutCorrelation_IsRefusedBeforeAdmission()
    {
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "the answer",
                msg_type = "response",
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be("missing_correlation");
    }

    [Fact]
    public async Task SendMessage_TaskUpdateWithoutCorrelation_IsRefusedBeforeAdmission()
    {
        // Progress on nothing is not progress. Admitted bare, it would reach the receiver with no way
        // to tell which delegation it was about.
        var root = CreateRegisteredRoot();
        var (_, helperSetup) = RegisterPeer(root, "helper");
        // Seeded so the refusal under test is the missing correlation, not the absence of any
        // delegation to correlate to (#689 gates task_update on that first).
        _ = await DelegateAsync(helperSetup, root.AgentId);
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "half done",
                msg_type = "task_update",
            }
        );

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
        _ = root.Directory.TryUpdateStatus(peerSetup.AgentId, AgentCollaborationStatuses.Completed);
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "focus on the parser instead",
                msg_type = "steer",
            }
        );

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

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "still needs to arrive",
                msg_type = "question",
            },
            cts.Token
        );

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

        root.Directory.FindById(childId)!.Status.Should().Be(AgentCollaborationStatuses.Completed);

        var dispatch = new AgentCollaborationMessenger(root).Send(childId, "One more thing", AgentMessageType.Question);
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
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                Task.FromResult(
                    ToAsyncEnumerable([
                        new UsageMessage
                        {
                            Usage = new Usage { PromptTokens = 10, CompletionTokens = 5 },
                            GenerationId = "gen-1",
                        },
                        new TextMessage { Text = "never observed", Role = Role.Assistant },
                    ])
                )
            );

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
            childId,
            "Which branch did you use?",
            AgentMessageType.Question
        );
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

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "hello",
                msg_type = "shout",
            }
        );

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

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new Dictionary<string, object?>
            {
                ["target"] = "helper",
                ["content"] = "hello",
                ["msg_type"] = msgType,
            }
        );

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

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new Dictionary<string, object?>
            {
                ["target"] = "helper",
                ["content"] = "hello",
                ["msg_type"] = msgType,
                ["in_response_to"] = null,
            }
        );

        payload.IsError.Should().BeFalse(payload.Text);
    }

    [Theory]
    [InlineData("question")]
    [InlineData("delegate_task")]
    [InlineData("steer")]
    public async Task SendMessage_NonReplyType_WithEmptyStringCorrelation_NormalizesToAbsentAndSucceeds(string msgType)
    {
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new Dictionary<string, object?>
            {
                ["target"] = "helper",
                ["content"] = "hello",
                ["msg_type"] = msgType,
                ["in_response_to"] = "",
            }
        );

        payload.IsError.Should().BeFalse(payload.Text);
    }

    [Theory]
    [InlineData("question")]
    [InlineData("delegate_task")]
    [InlineData("steer")]
    public async Task SendMessage_NonReplyType_WithWhitespaceCorrelation_NormalizesToAbsentAndSucceeds(string msgType)
    {
        var root = CreateRegisteredRoot();
        _ = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new Dictionary<string, object?>
            {
                ["target"] = "helper",
                ["content"] = "hello",
                ["msg_type"] = msgType,
                ["in_response_to"] = "   ",
            }
        );

        payload.IsError.Should().BeFalse(payload.Text);
    }

    [Theory]
    [InlineData("response")]
    [InlineData("task_update")]
    public async Task SendMessage_ReplyType_WithJsonNullCorrelation_ReturnsMissingCorrelation(string msgType)
    {
        var root = CreateRegisteredRoot();
        var (_, helperSetup) = RegisterPeer(root, "helper");
        // Seeded so the refusal under test is the missing correlation, not the absence of any
        // delegation to correlate to (#689 gates task_update on that first).
        _ = await DelegateAsync(helperSetup, root.AgentId);
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new Dictionary<string, object?>
            {
                ["target"] = "helper",
                ["content"] = "hello",
                ["msg_type"] = msgType,
                ["in_response_to"] = null,
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.MissingCorrelation);
    }

    [Theory]
    [InlineData("response")]
    [InlineData("task_update")]
    public async Task SendMessage_ReplyType_WithEmptyStringCorrelation_ReturnsMissingCorrelation(string msgType)
    {
        var root = CreateRegisteredRoot();
        var (_, helperSetup) = RegisterPeer(root, "helper");
        // Seeded so the refusal under test is the missing correlation, not the absence of any
        // delegation to correlate to (#689 gates task_update on that first).
        _ = await DelegateAsync(helperSetup, root.AgentId);
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new Dictionary<string, object?>
            {
                ["target"] = "helper",
                ["content"] = "hello",
                ["msg_type"] = msgType,
                ["in_response_to"] = "",
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.MissingCorrelation);
    }

    [Theory]
    [InlineData("response")]
    [InlineData("task_update")]
    public async Task SendMessage_ReplyType_WithWhitespaceCorrelation_ReturnsMissingCorrelation(string msgType)
    {
        var root = CreateRegisteredRoot();
        var (_, helperSetup) = RegisterPeer(root, "helper");
        // Seeded so the refusal under test is the missing correlation, not the absence of any
        // delegation to correlate to (#689 gates task_update on that first).
        _ = await DelegateAsync(helperSetup, root.AgentId);
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new Dictionary<string, object?>
            {
                ["target"] = "helper",
                ["content"] = "hello",
                ["msg_type"] = msgType,
                ["in_response_to"] = "   ",
            }
        );

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

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "the answer",
                msg_type = "response",
                in_response_to = "agentmsg-does-not-exist",
            }
        );

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

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "hello",
                msg_type = "question",
                in_response_to = "agentmsg-does-not-exist",
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.UnknownCorrelation);
    }

    #endregion

    #region task_update is gated on an open inbound delegation (#689)

    [Fact]
    public void SendMessageDescriptor_SaysTaskUpdateNeedsAnOpenDelegationAndWhereItsIdComesFrom()
    {
        // The schema is built once per loop, before any delegation can have arrived, so the enum cannot
        // be narrowed per turn. The prose is therefore the only place the model can learn that
        // task_update is conditional, and that its id is minted by the envelope it received — not
        // something to guess.
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var parameters = provider.GetFunctions().First(f => f.Contract.Name == "SendMessage").Contract.Parameters!;

        parameters.First(p => p.Name == "msg_type").Description.Should().Contain("open delegated task");
        parameters
            .First(p => p.Name == "in_response_to")
            .Description.Should()
            .Contain("DelegateTask")
            .And.Contain("message-id");
    }

    [Fact]
    public async Task SendMessage_TaskUpdate_AgainstAnOpenDelegation_IsAcceptedAndDelivered()
    {
        var root = CreateRegisteredRoot();
        var (helperEndpoint, helperSetup) = RegisterPeer(root, "helper");
        var delegationId = await DelegateAsync(helperSetup, root.AgentId);
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "half done",
                msg_type = "task_update",
                in_response_to = delegationId,
            }
        );

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");

        var received = await helperEndpoint.Received.WaitAsync(TimeSpan.FromSeconds(10));
        received.AgentMessageType.Should().Be(AgentMessageType.TaskUpdate);
        received.InResponseTo.Should().Be(delegationId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("none")]
    [InlineData("agentmsg-does-not-exist")]
    public async Task SendMessage_TaskUpdate_WithNoOpenDelegation_IsRefusedWithTheNextValidAction(string? inResponseTo)
    {
        // A freshly spawned agent was never delegated to through the ledger, so it cannot hold the
        // correlation task_update needs. Telling it only that the id was "not received" invited guesses
        // like 'none' or 'TODO'; the refusal has to say the type is unavailable and name what to send
        // instead.
        var root = CreateRegisteredRoot();
        var (helperEndpoint, _) = RegisterPeer(root, "helper");
        var (_, provider) = CreateManager(root);
        var ledgerCountBefore = root.Bundle.Ledger.Count;

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new Dictionary<string, object?>
            {
                ["target"] = "helper",
                ["content"] = "half done",
                ["msg_type"] = "task_update",
                ["in_response_to"] = inResponseTo,
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.NoOpenDelegation);
        payload.Text.Should().Contain("no open delegated task").And.Contain("'response'").And.Contain("'question'");

        // Non-vacuity: refused means nothing was admitted and nothing reached the target.
        root.Bundle.Ledger.Count.Should().Be(ledgerCountBefore);
        root.Bundle.Ledger.GetOpenOutbound(root.AgentId).Should().BeEmpty();
        helperEndpoint.Received.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task SendMessage_TaskUpdate_WithAWrongCorrelation_ListsTheOpenDelegationIds()
    {
        // Ids are not content: echoing the ones the sender may report on turns a dead end into the
        // next call.
        var root = CreateRegisteredRoot();
        var (helperEndpoint, helperSetup) = RegisterPeer(root, "helper");
        var delegationId = await DelegateAsync(helperSetup, root.AgentId);
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "half done",
                msg_type = "task_update",
                in_response_to = "agentmsg-does-not-exist",
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.UnknownCorrelation);
        payload.Text.Should().Contain(delegationId);
        helperEndpoint.Received.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task SendMessage_TaskUpdate_AgainstAnAnsweredDelegation_ListsTheStillOpenDelegationIds()
    {
        // The "wrong id" case is not only an id that never existed. An agent holding several delegations
        // reports on the one it just finished far more readily than on one that never existed, and that
        // refusal has to name the delegations still open just as the unknown-id one does — otherwise the
        // agent whose mistake was picking the wrong one of ITS OWN ids is told nothing it did not know.
        var root = CreateRegisteredRoot();
        var (_, helperSetup) = RegisterPeer(root, "helper");
        var answeredId = await DelegateAsync(helperSetup, root.AgentId);
        var stillOpenId = await DelegateAsync(helperSetup, root.AgentId);
        var (_, provider) = CreateManager(root);

        // Answered through the messenger with its delivery awaited, because the correlation is settled
        // when the reply LANDS. Sending it through the tool returns as soon as it is admitted, and the
        // classifier then still reads a reply in flight rather than an answered delegation.
        var answer = new AgentCollaborationMessenger(root).Send(
            helperSetup.AgentId,
            "the first one is done",
            AgentMessageType.Response,
            answeredId
        );
        answer.Result.Succeeded.Should().BeTrue(answer.Result.FailureCode);
        await answer.Delivery.WaitAsync(TimeSpan.FromSeconds(10));
        root.Bundle.Ledger.GetOpenInboundDelegations(root.AgentId)
            .Select(e => e.MessageId)
            .Should()
            .BeEquivalentTo([stillOpenId], "the answered delegation must be closed before the refusal is exercised");

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "still working on it",
                msg_type = "task_update",
                in_response_to = answeredId,
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.CorrelationAnswered);
        payload.Text.Should().Contain(stillOpenId);

        // The id it wrongly named must NOT be offered back as a thing it may report on.
        payload.Text.Should().NotContain($"'{answeredId}'");
    }

    [Fact]
    public async Task SendMessage_TaskUpdate_WithoutCorrelationButWithAnOpenDelegation_ListsTheOpenDelegationIds()
    {
        var root = CreateRegisteredRoot();
        var (_, helperSetup) = RegisterPeer(root, "helper");
        var delegationId = await DelegateAsync(helperSetup, root.AgentId);
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(
            provider,
            "SendMessage",
            new
            {
                target = "helper",
                content = "half done",
                msg_type = "task_update",
            }
        );

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(AgentMessageFailureCodes.MissingCorrelation);
        payload.Text.Should().Contain(delegationId);
    }

    #endregion

    #region CheckAgents and WaitForAgents

    [Fact]
    public async Task CheckAgents_ObservesEveryListedAgentInOneCall()
    {
        var (_, provider) = CreateManager(CreateRegisteredRoot());
        var first = await SpawnAndResolveIdAsync(provider, "one");
        var second = await SpawnAndResolveIdAsync(provider, "two");

        var payload = await InvokeAsync(provider, "CheckAgents", new { agent_ids = $"{first},{second}" });

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

        var payload = await InvokeAsync(provider, "CheckAgents", new { agent_ids = peerSetup.AgentId });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("not_found").GetInt32().Should().Be(0);
        var observed = doc.RootElement.GetProperty("agents").EnumerateArray().Single();
        observed.GetProperty("name").GetString().Should().Be("cousin");
        observed.GetProperty("status").GetString().Should().Be(AgentCollaborationStatuses.Running);
    }

    /// <summary>
    /// The pull half of delivery observability: what the sender still has to act on, listed beside the
    /// agents it is asking about.
    /// </summary>
    /// <remarks>
    /// The push notice can be missed — a sender that was not running when its delivery failed is never
    /// woken, and one that was running has to notice a message among its inputs. This is the view it can
    /// ask for, which is why it has to carry the states an "open obligations" list would drop: a
    /// delivery failure CLOSES the ledger entry, so the moment the sender most needs to see the message
    /// is the moment an open-only view stops showing it.
    /// </remarks>
    [Fact]
    public async Task CheckAgents_ListsWhatTheSenderStillOwes_IncludingDeliveriesThatFailedAfterAcceptance()
    {
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);
        var messenger = new AgentCollaborationMessenger(root);

        // Three targets covering the three things a sender can be left holding: a message that can
        // never arrive, one that could arrive on a retry, and one that arrived and is awaiting a reply.
        var gone = RegisterPeerWithEndpoint(root, "gone", endpoint: null);
        var busy = RegisterPeerWithEndpoint(
            root,
            "busy",
            new StubEndpoint(AgentDeliveryDisposition.Refused, "input_queue_full")
        );
        var (_, live) = RegisterPeer(root, "live");

        var toGone = messenger.Send(gone.AgentId, "are you there?", AgentMessageType.Question);
        var toBusy = messenger.Send(busy.AgentId, "take this", AgentMessageType.DelegateTask);
        var toLive = messenger.Send(live.AgentId, "still working?", AgentMessageType.Question);
        await Task.WhenAll(toGone.Delivery, toBusy.Delivery, toLive.Delivery).WaitAsync(TimeSpan.FromSeconds(10));

        var payload = await InvokeAsync(
            provider,
            "CheckAgents",
            new { agent_ids = $"{gone.AgentId},{busy.AgentId},{live.AgentId}" }
        );

        using var doc = JsonDocument.Parse(payload.Text);
        var outbound = doc.RootElement.GetProperty("outbound");
        outbound.GetProperty("count").GetInt32().Should().Be(3);
        var rows = outbound.GetProperty("messages").EnumerateArray().ToDictionary(r => Text(r, "message_id"));

        var goneRow = rows[toGone.Result.MessageId!];
        goneRow.GetProperty("state").GetString().Should().Be("delivery_failed");
        goneRow.GetProperty("to_agent_id").GetString().Should().Be(gone.AgentId);
        goneRow.GetProperty("msg_type").GetString().Should().Be("question");
        goneRow.GetProperty("reason").GetString().Should().Be(AgentCollaborationMessenger.NoEndpointReasonCode);

        // The one distinction the model acts on: this message is worth sending again and the other
        // failure is not. Without it both read as "failed" and a sender either gives up on a
        // recoverable message or retries a hopeless one forever.
        goneRow.GetProperty("retryable").GetBoolean().Should().BeFalse();
        var busyRow = rows[toBusy.Result.MessageId!];
        busyRow.GetProperty("state").GetString().Should().Be("delivery_failed");
        busyRow.GetProperty("reason").GetString().Should().Be(AgentCollaborationMessenger.TargetBusyRetryReasonCode);
        busyRow.GetProperty("retryable").GetBoolean().Should().BeTrue();

        // A message that has NOT failed says nothing about retrying. 'retryable: false' here would be
        // read by anyone who skims past 'state' as "this one cannot be sent again", which is the
        // opposite of the truth: it is in flight and the sender should simply wait.
        var liveRow = rows[toLive.Result.MessageId!];
        liveRow.GetProperty("state").GetString().Should().Be("delivered");
        liveRow.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
        liveRow.GetProperty("retryable").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task CheckAgents_DropsAQuestionOnceItHasBeenAnswered()
    {
        // The list exists to be acted on, so anything that needs nothing must leave it. A view that
        // kept answered questions would grow for the whole session and bury the entries that matter.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);
        var (_, live) = RegisterPeer(root, "live");

        var question = new AgentCollaborationMessenger(root).Send(
            live.AgentId,
            "which branch?",
            AgentMessageType.Question
        );
        await question.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        var answer = new AgentCollaborationMessenger(live).Send(
            root.AgentId,
            "main",
            AgentMessageType.Response,
            question.Result.MessageId
        );
        await answer.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        var payload = await InvokeAsync(provider, "CheckAgents", new { agent_ids = live.AgentId });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.TryGetProperty("outbound", out _).Should().BeFalse("nothing is outstanding toward 'live'");
    }

    [Fact]
    public async Task CheckAgents_OnlyListsObligationsTowardTheAgentsItWasAskedAbout()
    {
        // CheckAgents is a question about named agents, and the answer stays proportional to it. An
        // unscoped list would grow with the whole collaboration and put an unrelated agent's message in
        // front of the model every time it checked on anything at all.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);
        var gone = RegisterPeerWithEndpoint(root, "gone", endpoint: null);
        var (_, live) = RegisterPeer(root, "live");

        var toGone = new AgentCollaborationMessenger(root).Send(gone.AgentId, "hello?", AgentMessageType.Question);
        await toGone.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        var payload = await InvokeAsync(provider, "CheckAgents", new { agent_ids = live.AgentId });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.TryGetProperty("outbound", out _).Should().BeFalse();
        root.Bundle.Ledger.GetUnsettledOutbound(root.AgentId)
            .Should()
            .ContainSingle("the obligation still exists — it is simply not what this call asked about");
    }

    private static string Text(JsonElement element, string property) => element.GetProperty(property).GetString()!;

    /// <summary>
    /// A question repeated under one idempotency key leaves the recipient owing ONE answer.
    /// </summary>
    /// <remarks>
    /// A duplicate send is not a wasted call: every admitted question is an obligation somebody now has
    /// to answer, and the second copy makes the recipient answer twice while the sender waits for two
    /// replies to a question it asked once. Neither can be withdrawn once admitted, which is why the
    /// guard has to sit in front of admission rather than after it.
    /// </remarks>
    [Fact]
    public async Task SendMessage_ReplayedUnderTheSameIdempotencyKey_CreatesOneObligation()
    {
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);
        var (_, live) = RegisterPeer(root, "live");
        var args = new
        {
            target = live.AgentId,
            content = "which branch?",
            msg_type = "question",
            idempotency_key = "ask-1",
        };

        var first = await InvokeAsync(provider, "SendMessage", args);
        var second = await InvokeAsync(provider, "SendMessage", args);

        root.Bundle.Ledger.GetOpenInbound(live.AgentId).Should().ContainSingle();

        using var firstDoc = JsonDocument.Parse(first.Text);
        using var secondDoc = JsonDocument.Parse(second.Text);
        var messageId = Text(firstDoc.RootElement, "message_id");
        Text(secondDoc.RootElement, "message_id").Should().Be(messageId);
        Text(secondDoc.RootElement.GetProperty("replay"), "code")
            .Should()
            .Be(SubAgentToolProvider.IdempotentReplayCode);

        // The replayed receipt is still a receipt: a caller reading only the fields it read the first
        // time round must not find them missing or changed.
        Text(secondDoc.RootElement, "status").Should().Be("accepted");
        secondDoc.RootElement.GetProperty("expects_reply").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task SendMessage_RepeatedWithoutAKey_CreatesTwoObligations()
    {
        // The half that keeps the key honest as an opt-in: asking the same thing twice on purpose is a
        // legitimate act, and nothing may quietly collapse it into one.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);
        var (_, live) = RegisterPeer(root, "live");
        var args = new
        {
            target = live.AgentId,
            content = "which branch?",
            msg_type = "question",
        };

        _ = await InvokeAsync(provider, "SendMessage", args);
        _ = await InvokeAsync(provider, "SendMessage", args);

        root.Bundle.Ledger.GetOpenInbound(live.AgentId).Should().HaveCount(2);
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

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = $"{agentId},ghost" });

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
            root.AgentId,
            "Which branch?",
            AgentMessageType.Question
        );
        dispatch.Result.Succeeded.Should().BeTrue();

        // Settle the delivery before waiting, so the question is unambiguously open by the time the
        // sweep runs rather than racing a background delivery that would close it on failure.
        await dispatch.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("question_received");
        doc.RootElement.GetProperty("question").GetProperty("from_name").GetString().Should().Be("asker");
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
            root.AgentId,
            "Which branch?",
            AgentMessageType.Question
        );
        await dispatch.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        var first = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId });
        using var firstDoc = JsonDocument.Parse(first.Text);
        firstDoc.RootElement.GetProperty("status").GetString().Should().Be("question_received");

        var second = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId, timeout_seconds = 1 });

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
            configure: options =>
                options with
                {
                    Templates = new Dictionary<string, SubAgentTemplate>(options.Templates)
                    {
                        ["blocker"] = BlockingTemplate(),
                    },
                }
        );

        var finishedId = await SpawnAndResolveIdAsync(provider, "finished");
        var blockedId = await SpawnAndResolveIdAsync(provider, "still-running", "blocker");

        // Settle the finished child while no question exists, so the wait that loses below is racing an
        // observation that was already complete before the race began.
        using (
            var settled = JsonDocument.Parse(
                (await InvokeAsync(provider, "WaitForAgents", new { agent_ids = finishedId })).Text
            )
        )
        {
            settled.RootElement.GetProperty("status").GetString().Should().Be("completed");
        }

        var (_, peerSetup) = RegisterPeer(root, "asker");
        var dispatch = new AgentCollaborationMessenger(peerSetup).Send(
            root.AgentId,
            "Which branch?",
            AgentMessageType.Question
        );
        await dispatch.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        var lost = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = finishedId });
        using var lostDoc = JsonDocument.Parse(lost.Text);
        lostDoc.RootElement.GetProperty("status").GetString().Should().Be("completed");
        lostDoc
            .RootElement.GetProperty("question")
            .ValueKind.Should()
            .Be(JsonValueKind.Null, "the completion won, so no question is being reported to the caller");

        // The next wait has nothing to complete, so only the question can end it — which it can only do
        // if the claim the previous wait took but never used was returned.
        var interrupted = await InvokeAsync(
            provider,
            "WaitForAgents",
            new { agent_ids = blockedId, timeout_seconds = 5 }
        );

        using var interruptedDoc = JsonDocument.Parse(interrupted.Text);
        interruptedDoc.RootElement.GetProperty("status").GetString().Should().Be("question_received");
        interruptedDoc.RootElement.GetProperty("question").GetProperty("from_name").GetString().Should().Be("asker");

        root.Bundle.Ledger.GetOpenInbound(root.AgentId)
            .Should()
            .ContainSingle("being interrupted by a question is still not an answer to it");
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
            root.AgentId,
            "Which branch?",
            AgentMessageType.Question
        );
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

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId, timeout_seconds = 1 });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("timeout");
        manager
            .TryPeek(agentId, out _)
            .Should()
            .BeTrue(because: "a wait that expires abandons the observation, never the agent");
    }

    [Fact]
    public async Task WaitForAgents_IsInterruptedByADelegatedTaskAddressedToTheWaiter()
    {
        // A delegation is the same obligation a question is — it stays open until this agent answers,
        // and its sender is blocked meanwhile. Waking only for questions meant a delegator could be
        // parked behind a wait that had no way to end until the delegation it was owed was answered.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root, template: BlockingTemplate());
        var agentId = await SpawnAndResolveIdAsync(provider);
        var (_, peerSetup) = RegisterPeer(root, "delegator");

        var delegationId = await DelegateAsync(peerSetup, root.AgentId);

        // Capped so the defect this pins reads as a failure rather than as a hung suite: before the
        // fix the child blocks forever and nothing but the cap can end the wait.
        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId, timeout_seconds = 5 });

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("interrupted");

        var interrupt = doc.RootElement.GetProperty("interrupt");
        interrupt.GetProperty("message_id").GetString().Should().Be(delegationId);
        interrupt.GetProperty("from_name").GetString().Should().Be("delegator");
        interrupt
            .GetProperty("msg_type")
            .GetString()
            .Should()
            .Be("delegate_task", "the waiter has to know which kind it is answering to reply correctly");

        // The pre-rename field is still filled for one release, so a reader of either name sees it.
        doc.RootElement.GetProperty("question").GetProperty("message_id").GetString().Should().Be(delegationId);

        // Interrupting is not answering: the delegation is still owed a response.
        root.Bundle.Ledger.GetOpenInbound(root.AgentId).Should().ContainSingle();
    }

    [Fact]
    public async Task WaitForAgents_InterruptedByAQuestion_NamesItsKindToo()
    {
        // The status alone no longer identifies the kind, so the discriminator has to be present on
        // both branches — a question that reported no msg_type would leave the waiter guessing.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root, template: BlockingTemplate());
        var agentId = await SpawnAndResolveIdAsync(provider);
        var (_, peerSetup) = RegisterPeer(root, "asker");

        var dispatch = new AgentCollaborationMessenger(peerSetup).Send(
            root.AgentId,
            "Which branch?",
            AgentMessageType.Question
        );
        await dispatch.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("question_received");
        doc.RootElement.GetProperty("interrupt").GetProperty("msg_type").GetString().Should().Be("question");
    }

    [Fact]
    public async Task WaitForAgents_IsNotInterruptedByASteerAddressedToTheWaiter()
    {
        // The interrupt set is exactly the kinds whose sender stays blocked. A steer closes on
        // delivery and owes nothing back, so waking for one would make every wait unpredictable
        // without unblocking anybody.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root, template: BlockingTemplate());
        var agentId = await SpawnAndResolveIdAsync(provider);
        var (_, peerSetup) = RegisterPeer(root, "nudger");

        // Started without awaiting: the handler runs synchronously as far as subscribing its watcher
        // to the ledger, so the steer below is unambiguously admitted while the wait is listening —
        // the admission path is the only one a steer could ever reach, since delivery closes it before
        // any later sweep could see it.
        var wait = InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId, timeout_seconds = 2 });

        var dispatch = new AgentCollaborationMessenger(peerSetup).Send(
            root.AgentId,
            "try the other branch",
            AgentMessageType.Steer
        );
        dispatch.Result.Succeeded.Should().BeTrue(dispatch.Result.FailureCode);

        var payload = await wait;

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("timeout");
    }

    [Fact]
    public async Task WaitForAgents_WithAPeerTarget_WaitsOnTheChildrenAndReportsThePeerAsNotWaited()
    {
        // Naming a real agent that is not yours to wait on is not the same mistake as naming nothing:
        // the agent exists and can be messaged. Refusing the whole call taught the model nothing, so
        // the children are waited on and the peer comes back with the action that does apply.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);
        var childId = await SpawnAndResolveIdAsync(provider);
        var (_, peerSetup) = RegisterPeer(root, "cousin");

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = $"{childId},cousin" });

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");

        var skipped = doc.RootElement.GetProperty("not_waited").EnumerateArray().Single();
        skipped.GetProperty("target").GetString().Should().Be("cousin");
        skipped.GetProperty("agent_id").GetString().Should().Be(peerSetup.AgentId);
        skipped.GetProperty("reason").GetString().Should().Be("peer_not_child");
        skipped.GetProperty("next_action").GetString().Should().NotBeNullOrWhiteSpace();

        // Only the child was waited on and observed; the peer's status is CheckAgents' job.
        doc.RootElement.GetProperty("agents").GetProperty("requested").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task WaitForAgents_OnPeersOnly_ReportsNotWaitableWithoutRefusingTheCall()
    {
        // Non-error on purpose: a refusal here counts as a retry storm in run-level analysis, when
        // what actually happened is an observation that names its own next action.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);
        var (_, peerSetup) = RegisterPeer(root, "cousin");

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = "cousin" });

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("not_waitable");
        doc.RootElement.GetProperty("not_waited")
            .EnumerateArray()
            .Single()
            .GetProperty("agent_id")
            .GetString()
            .Should()
            .Be(peerSetup.AgentId);
    }

    [Fact]
    public async Task WaitForAgents_WhenATargetIsBlockedOnAnAnswerFromTheWaiter_ReportsAWaitCycle()
    {
        // The deadlock the one-shot interrupt cannot fix. The child asked its parent something and
        // cannot finish until it is answered, so waiting on that child can only ever time out — and
        // because the interrupt is spent after one wait, every later wait would block in silence.
        // Reported rather than claimed, so the answer stays the same for as long as the cycle does.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root, template: BlockingTemplate());
        var childId = await SpawnAndResolveIdAsync(provider);

        var sent = root.Bundle.TrySend(childId, root.AgentId, AgentMessageType.Question);
        sent.Succeeded.Should().BeTrue(sent.FailureCode);

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = childId, timeout_seconds = 1 });

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("wait_cycle");
        doc.RootElement.GetProperty("cycle_kind").GetString().Should().Be("unanswered_inbound");

        var blocking = doc.RootElement.GetProperty("blocking").EnumerateArray().Single();
        blocking.GetProperty("message_id").GetString().Should().Be(sent.MessageId);
        blocking.GetProperty("from_agent_id").GetString().Should().Be(childId);
        blocking.GetProperty("msg_type").GetString().Should().Be("question");
        doc.RootElement.GetProperty("next_action").GetString().Should().Contain(sent.MessageId!);

        // Reporting the cycle neither answers the question nor spends its interrupt, so a second wait
        // says the same thing instead of blocking.
        var again = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = childId, timeout_seconds = 1 });
        using var againDoc = JsonDocument.Parse(again.Text);
        againDoc.RootElement.GetProperty("status").GetString().Should().Be("wait_cycle");
        root.Bundle.Ledger.GetOpenInbound(root.AgentId).Should().ContainSingle();
    }

    [Fact]
    public async Task WaitForAgents_WithAnUnansweredQuestionFromANonTarget_StillWaits()
    {
        // Narrowness of the cycle check: an obligation to somebody this wait is NOT blocked on is not
        // a cycle. It still interrupts — once — but it must not turn every wait into wait_cycle.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root, template: BlockingTemplate());
        var childId = await SpawnAndResolveIdAsync(provider);
        var (_, peerSetup) = RegisterPeer(root, "asker");

        var dispatch = new AgentCollaborationMessenger(peerSetup).Send(
            root.AgentId,
            "Which branch?",
            AgentMessageType.Question
        );
        await dispatch.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        var first = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = childId });
        using var firstDoc = JsonDocument.Parse(first.Text);
        firstDoc.RootElement.GetProperty("status").GetString().Should().Be("question_received");

        var second = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = childId, timeout_seconds = 1 });
        using var secondDoc = JsonDocument.Parse(second.Text);
        secondDoc.RootElement.GetProperty("status").GetString().Should().Be("timeout");
    }

    [Fact]
    public async Task WaitForAgents_InAnyMode_ReportsTheResultOfAChildThatFinishedBeforeTheBarrier()
    {
        // A batch wait that returns on the first finisher still has to hand back what the already
        // terminal children produced; losing it would make 'any' mode cost the work it saved.
        SetupSubAgentReply("the result that predates the barrier");

        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(
            root,
            configure: options =>
                options with
                {
                    Templates = new Dictionary<string, SubAgentTemplate>(options.Templates)
                    {
                        ["blocker"] = BlockingTemplate(),
                    },
                }
        );

        var finishedId = await SpawnAndResolveIdAsync(provider, "finished");
        var blockedId = await SpawnAndResolveIdAsync(provider, "still-running", "blocker");

        var payload = await InvokeAsync(
            provider,
            "WaitForAgents",
            new
            {
                agent_ids = $"{finishedId},{blockedId}",
                mode = "any",
                timeout_seconds = 10,
            }
        );

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");

        var finished = doc
            .RootElement.GetProperty("agents")
            .GetProperty("agents")
            .EnumerateArray()
            .Single(a => a.GetProperty("agent_id").GetString() == finishedId);
        finished.GetProperty("last_result").GetString().Should().Contain("the result that predates the barrier");
    }

    [Fact]
    public async Task WaitForAgents_OnAChildThatAskedAndThenFinished_ReportsItCompleted()
    {
        // The normal shape, not an edge case: SendMessage returns on admission, so a child that asks
        // its parent something and then ends its turn leaves that question open forever — a run that
        // merely finishes is deliberately not retired, so nothing abandons its outbound obligation.
        // Reading the cycle off the sender alone therefore turned every later wait on that terminal
        // child into wait_cycle, and told the coordinator to answer an agent that had already finished
        // — which restarts it. In 'all' mode one such target would suppress the outcome for every
        // healthy sibling.
        var root = CreateRegisteredRoot();
        var (manager, provider) = CreateManager(root, MessagingTemplate(root.AgentId));
        var childId = await SpawnAndResolveIdAsync(provider);

        _ = await manager
            .ObserveTargetCompletionAsync(childId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        // Non-vacuity: the child really is terminal AND really does still owe an answer, so the wait
        // below runs against the exact state that used to report a cycle.
        root.Bundle.Ledger.GetOpenInbound(root.AgentId)
            .Should()
            .ContainSingle()
            .Which.FromAgentId.Should()
            .Be(childId);

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = childId, timeout_seconds = 5 });

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status")
            .GetString()
            .Should()
            .Be("completed", "an agent that has already finished cannot be the far end of a deadlock");

        doc.RootElement.GetProperty("agents")
            .GetProperty("agents")
            .EnumerateArray()
            .Single()
            .GetProperty("last_result")
            .GetString()
            .Should()
            .Be("done", "the result the coordinator waited for has to survive the cycle check");
    }

    [Fact]
    public async Task WaitForAgents_WithAnInboundQuestionAlreadyBeingAnswered_NeitherReportsACycleNorInterrupts()
    {
        // Between a response's admission and its delivery the question it answers is still open, but
        // it is spoken for: ValidateCorrelation refuses a second reply with correlation_closed. Both
        // readers of the open-inbound set have to honour that claim, or the wait advises answering a
        // message that would be refused — as wait_cycle from the cycle check, or as question_received
        // from the watcher's opening sweep.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root, template: BlockingTemplate());
        var childId = await SpawnAndResolveIdAsync(provider);

        // TrySend admits without delivering, which is the whole window: a delivered response would
        // close the question instead of leaving it open with a claim on it.
        var question = root.Bundle.TrySend(childId, root.AgentId, AgentMessageType.Question);
        question.Succeeded.Should().BeTrue(question.FailureCode);

        var answer = root.Bundle.TrySend(root.AgentId, childId, AgentMessageType.Response, question.MessageId);
        answer.Succeeded.Should().BeTrue(answer.FailureCode);

        root.Bundle.Ledger.GetOpenInbound(root.AgentId)
            .Should()
            .ContainSingle()
            .Which.PendingResponseMessageId.Should()
            .Be(answer.MessageId, "the window this pins only exists while the reply is in flight");

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = childId, timeout_seconds = 1 });

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status")
            .GetString()
            .Should()
            .Be("timeout", "an obligation that is already being answered is neither a cycle nor an interrupt");
    }

    [Fact]
    public async Task WaitForAgents_OnItself_IsRefusedAsUnknown()
    {
        // The directory resolves the caller's own id, so without a guard an agent naming itself lands
        // in the peer partition and is told to send itself a message. That is not a valid next action,
        // and answering a self-wait with a non-error erases the storm signal the refusal carries.
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = root.AgentId });

        payload.IsError.Should().BeTrue(payload.Text);
        payload.ErrorCode.Should().Be("unknown_agent");
        payload.Text.Should().Contain(root.AgentId);
    }

    [Fact]
    public void WaitForAgentsDescriptor_SaysNotWaitableMeansRealAgentsThatAreNotYourChildren()
    {
        // not_waitable is reached only when every name resolved to a real agent. A set of typos still
        // refuses the whole call with unknown_agent, so a description that says "nothing you named is
        // one of your own children" describes an outcome the model will never see for that input.
        var (_, provider) = CreateManager(CreateRegisteredRoot());

        var description = Description(provider, "WaitForAgents");

        description.Should().NotContain("nothing you named is one of your own children");
        description
            .Should()
            .Contain(
                "every agent you named is real but none of them is one of your own children",
                "the status has to be told apart from the refusal a name that matches nothing gets"
            );
    }

    #endregion

    #region Helpers

    /// <summary>The one spawn name a workflow-style <c>SpawnNameGate</c> in these tests will allow.</summary>
    private const string AuthoredUnit = "authored-unit";

    private static object NewSpawn(string name, string subagentType = "worker") =>
        new
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
    private static AgentCollaborationSetup CreateRegisteredRoot(AgentCollaborationOptions? options = null)
    {
        var setup = AgentCollaborationSetup.CreateRoot(options ?? new AgentCollaborationOptions());
        _ = setup.Directory.TryRegister(
            setup.Context,
            setup.Name,
            AgentCollaborationStatuses.Running,
            new RecordingEndpoint()
        );
        return setup;
    }

    private (RecordingEndpoint Endpoint, AgentCollaborationSetup Setup) RegisterPeer(
        AgentCollaborationSetup root,
        string name
    )
    {
        var endpoint = new RecordingEndpoint();
        return (endpoint, RegisterPeerWithEndpoint(root, name, endpoint));
    }

    /// <summary>
    /// Registers a peer whose delivery behaviour the test chooses — including a peer with NO write
    /// endpoint, which is how an addressable agent that nothing can be handed to is spelled.
    /// </summary>
    private static AgentCollaborationSetup RegisterPeerWithEndpoint(
        AgentCollaborationSetup root,
        string name,
        IAgentWriteEndpoint? endpoint
    )
    {
        var context = root.Context.CreateChild(
            $"agent-{name}",
            AgentKind.SubAgent,
            $"{name} role",
            $"Stands in for {name}."
        );

        _ = root.Directory.TryAcquireCapacity(context.AgentId);
        root.Directory.TryRegister(context, name, AgentCollaborationStatuses.Running, endpoint)
            .Succeeded.Should()
            .BeTrue();

        return root.ForChild(context, name);
    }

    /// <summary>
    /// Delegates a task from one agent to another through the real messenger and settles its delivery,
    /// so the delegation is unambiguously open — and the recipient may report progress on it — by the
    /// time the test acts.
    /// </summary>
    private static async Task<string> DelegateAsync(AgentCollaborationSetup from, string toAgentId)
    {
        var dispatch = new AgentCollaborationMessenger(from).Send(
            toAgentId,
            "do the thing",
            AgentMessageType.DelegateTask
        );
        dispatch.Result.Succeeded.Should().BeTrue(dispatch.Result.FailureCode);
        await dispatch.Delivery.WaitAsync(TimeSpan.FromSeconds(10));
        return dispatch.Result.MessageId!;
    }

    private (SubAgentManager Manager, SubAgentToolProvider Provider) CreateManager(
        AgentCollaborationSetup? collaboration,
        SubAgentTemplate? template = null,
        Func<SubAgentOptions, SubAgentOptions>? configure = null,
        IUsageSink? usageSink = null
    )
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] =
                    template
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
            collaboration: collaboration
        );

        _managers.Add(manager);
        return (manager, new SubAgentToolProvider(manager, source));
    }

    /// <summary>A template whose agent cannot be built, standing in for a bad model or a dead provider.</summary>
    private static SubAgentTemplate FailingTemplate() =>
        new()
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
    private static SubAgentTemplate MessagingTemplate(string target) =>
        new()
        {
            SystemPrompt = "You are a worker.",
            AgentFactory = () =>
            {
                var turn = 0;
                var mock = new Mock<IStreamingAgent>();
                _ = mock.Setup(a =>
                        a.GenerateReplyStreamingAsync(
                            It.IsAny<IEnumerable<IMessage>>(),
                            It.IsAny<GenerateReplyOptions>(),
                            It.IsAny<CancellationToken>()
                        )
                    )
                    .Returns(() =>
                        Task.FromResult(
                            ToAsyncEnumerable(
                                Interlocked.Increment(ref turn) == 1
                                    ?
                                    [
                                        new ToolCallMessage
                                        {
                                            FunctionName = "SendMessage",
                                            FunctionArgs = JsonSerializer.Serialize(
                                                new
                                                {
                                                    target,
                                                    content = LeafGreeting,
                                                    // A question stands on its own. The reply-only types (response,
                                                    // task_update) are refused without an in_response_to, and this
                                                    // template has nothing to correlate to.
                                                    msg_type = "question",
                                                }
                                            ),
                                            ToolCallId = "tc_1",
                                            Role = Role.Assistant,
                                        },
                                    ]
                                    : [new TextMessage { Text = "done", Role = Role.Assistant }]
                            )
                        )
                    );
                return mock.Object;
            },
        };

    private SubAgentTemplate BlockingTemplate() =>
        new()
        {
            SystemPrompt = "You are a worker.",
            AgentFactory = () =>
            {
                var mock = new Mock<IStreamingAgent>();
                _ = mock.Setup(a =>
                        a.GenerateReplyStreamingAsync(
                            It.IsAny<IEnumerable<IMessage>>(),
                            It.IsAny<GenerateReplyOptions>(),
                            It.IsAny<CancellationToken>()
                        )
                    )
                    .Returns<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                        (_, _, ct) => Task.FromResult(BlockingStream(ct))
                    );
                return mock.Object;
            },
        };

    private async Task<string> SpawnAndResolveIdAsync(
        SubAgentToolProvider provider,
        string name = "child",
        string subagentType = "worker"
    )
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
                    Contract = new FunctionContract { Name = ToolName, Description = "Acts as exactly one agent." },
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
        CancellationToken ct = default
    )
    {
        var handler = provider.GetFunctions().First(f => f.Contract.Name == toolName).Handler;
        var result = await handler(JsonSerializer.Serialize(args), new ToolCallContext(), ct);

        return result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
    }

    private void SetupSubAgentReply(string text)
    {
        _ = _subAgentMock
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.FromResult(ToAsyncEnumerable([new TextMessage { Text = text, Role = Role.Assistant }])));
    }

    /// <summary>
    /// Runs one turn of a REAL <see cref="MultiTurnAgentLoop"/> whose provider emits a single tool
    /// call for <paramref name="functionName"/>, and returns the result the loop produced for it.
    /// The loop is what decides how an unregistered name is answered, so nothing short of driving it
    /// can show which of the two answers a caller gets.
    /// </summary>
    private async Task<ToolCallResultMessage> DriveOneToolCallAsync(
        AgentCollaborationSetup collaboration,
        string functionName,
        bool suppressSpawning = false
    )
    {
        // The provider asks for the tool ONCE and then settles, so the run produces exactly one
        // result to assert on; repeating the call every turn would loop the agent until cancellation.
        List<IMessage> firstTurn =
        [
            new ToolCallMessage
            {
                FunctionName = functionName,
                FunctionArgs = "{}",
                ToolCallId = "tc_withdrawn",
                Role = Role.Assistant,
            },
        ];
        List<IMessage> laterTurns = [new TextMessage { Text = "Understood.", Role = Role.Assistant }];

        var turn = 0;
        var provider = new Mock<IStreamingAgent>();
        _ = provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() => Task.FromResult(ToAsyncEnumerable(turn++ == 0 ? firstTurn : laterTurns)));

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new()
                {
                    SystemPrompt = "You are a worker.",
                    Description = "Does work.",
                    AgentFactory = () => _subAgentMock.Object,
                },
            },
        };

        await using var loop = new MultiTurnAgentLoop(
            provider.Object,
            new FunctionRegistry(),
            threadId: "withdrawn-tool-thread",
            includeAskUserQuestionTool: false,
            includeNotifyClientTool: false,
            subAgentOptions: subAgentOptions,
            subAgentTemplateSource: new MutableSubAgentTemplateSource(subAgentOptions.Templates),
            collaboration: collaboration
        );

        loop.SubAgentTools!.GetFunctions()
            .Select(f => f.Contract.Name)
            .Should()
            .NotContain("Agent", "the withdrawal this test is about has to have happened");

        using var suppression = suppressSpawning ? loop.SubAgentTools!.SuppressSpawning() : null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        List<IMessage> emitted = [];
        await foreach (
            var message in loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "delegate this", Role = Role.User }]),
                cts.Token
            )
        )
        {
            emitted.Add(message);
        }

        await cts.CancelAsync();

        return emitted.OfType<ToolCallResultMessage>().Should().ContainSingle().Subject;
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<IMessage> BlockingStream([EnumeratorCancellation] CancellationToken ct)
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
        private readonly TaskCompletionSource<IReadOnlyList<IMessage>> _restarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        private int _runs;

        public RestartCapturingTemplate()
        {
            Template = new SubAgentTemplate
            {
                SystemPrompt = "You are a worker.",
                AgentFactory = () =>
                {
                    var mock = new Mock<IStreamingAgent>();
                    _ = mock.Setup(a =>
                            a.GenerateReplyStreamingAsync(
                                It.IsAny<IEnumerable<IMessage>>(),
                                It.IsAny<GenerateReplyOptions>(),
                                It.IsAny<CancellationToken>()
                            )
                        )
                        .Returns<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                            (messages, _, ct) => Task.FromResult(Run(messages, ct))
                        );
                    return mock.Object;
                },
            };
        }

        /// <summary>What the restarted run was given, once it has begun.</summary>
        public Task<IReadOnlyList<IMessage>> Restarted => _restarted.Task;

        public SubAgentTemplate Template { get; }

        private IAsyncEnumerable<IMessage> Run(IEnumerable<IMessage> messages, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _runs) == 1)
            {
                return ToAsyncEnumerable([new TextMessage { Text = "done", Role = Role.Assistant }]);
            }

            _ = _restarted.TrySetResult([.. messages]);
            return BlockingStream(ct);
        }
    }

    /// <summary>A stand-in for another agent's owner, so a delivery can be observed without a loop.</summary>
    /// <summary>An endpoint whose answer to every hand-off is fixed by the test that built it.</summary>
    private sealed class StubEndpoint(AgentDeliveryDisposition disposition, string? reasonCode = null)
        : IAgentWriteEndpoint
    {
        public ValueTask<AgentDeliveryOutcome> DeliverAsync(
            AgentMessage message,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(new AgentDeliveryOutcome(disposition, reasonCode));
    }

    private sealed class RecordingEndpoint : IAgentWriteEndpoint
    {
        private readonly TaskCompletionSource<AgentMessage> _received = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task<AgentMessage> Received => _received.Task;

        public ValueTask<AgentDeliveryOutcome> DeliverAsync(
            AgentMessage message,
            CancellationToken cancellationToken = default
        )
        {
            _ = _received.TrySetResult(message);
            return ValueTask.FromResult(new AgentDeliveryOutcome(AgentDeliveryDisposition.Delivered));
        }
    }

    #endregion
}
