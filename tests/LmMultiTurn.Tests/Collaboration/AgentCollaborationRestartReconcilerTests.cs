using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// What a restart does with the agents and obligations a previous process wrote down: names that were
/// real stay resolvable and say so, an agent this process is running keeps its live registration, and
/// nothing is delivered to anybody.
/// </summary>
public class AgentCollaborationRestartReconcilerTests
{
    private const string RootId = "conv-a";
    private const string RootAgentId = "agent-root";

    private static readonly DateTimeOffset Noon = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static AgentCollaborationBundle CreateBundle(string collaborationId = RootId) =>
        new(collaborationId, new AgentCollaborationOptions());

    private static AgentCollaborationContext RegisterRoot(AgentCollaborationBundle bundle)
    {
        var root = AgentCollaborationContext.ForRoot(bundle.CollaborationId, RootAgentId);
        bundle.Directory.TryRegister(root, "root", AgentCollaborationStatuses.Running).Succeeded.Should().BeTrue();
        return root;
    }

    private static CollaborationNodeRecord Node(
        string agentId,
        string name,
        string collaborationId = RootId,
        string status = AgentCollaborationStatuses.Running
    ) =>
        new()
        {
            AgentId = agentId,
            CollaborationId = collaborationId,
            Name = name,
            ParentAgentId = RootAgentId,
            AncestorAgentIds = [RootAgentId],
            Kind = AgentKind.SubAgent,
            Role = "reviewer",
            Description = "reviews diffs",
            StructuralDepth = 1,
            DelegationDepth = 1,
            Status = status,
            SpawnedAt = Noon.AddMinutes(-30),
        };

    private static AgentIdentityBindingSet Binding(
        IReadOnlyList<CollaborationNodeRecord> agents,
        IReadOnlyList<OpenObligationRecord>? obligations = null,
        string collaborationId = RootId
    ) =>
        new()
        {
            CollaborationId = collaborationId,
            RootAgentId = RootAgentId,
            CapturedAtUtc = Noon,
            Agents = agents,
            OpenObligations = obligations ?? [],
        };

    private static OpenObligationRecord Obligation(string toAgentId, string fromAgentId = RootAgentId) =>
        new()
        {
            MessageId = $"agentmsg-{toAgentId}",
            FromAgentId = fromAgentId,
            ToAgentId = toAgentId,
            MessageType = AgentMessageType.Question,
            AdmittedAt = Noon.AddMinutes(-10),
        };

    [Fact]
    public void Reconcile_InvalidatesAnAgentNoProcessIsRunning()
    {
        var bundle = CreateBundle();
        RegisterRoot(bundle);

        var report = AgentCollaborationRestartReconciler.Reconcile(bundle, Binding([Node("agent-1", "reviewer")]));

        report.Invalidated.Should().Equal("agent-1");
        report.Rebound.Should().BeEmpty();
        bundle.Directory.Resolve("reviewer").FailureCode.Should().Be(AgentDirectoryFailureCodes.TargetNotLive);
    }

    [Fact]
    public void Reconcile_ReboundsAnAgentThisProcessIsAlreadyRunning()
    {
        // The root is the case that always happens: the loop registers it before reconciliation runs,
        // and the persisted set names it too. Tombstoning it would refuse the one agent that IS live.
        var bundle = CreateBundle();
        RegisterRoot(bundle);

        var report = AgentCollaborationRestartReconciler.Reconcile(
            bundle,
            Binding([
                Node(RootAgentId, "root", status: AgentCollaborationStatuses.Running),
                Node("agent-1", "reviewer"),
            ])
        );

        report.Rebound.Should().Equal(RootAgentId);
        report.Invalidated.Should().Equal("agent-1");
        bundle.Directory.Resolve("root").Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Reconcile_RefusesASetThatDescribesADifferentCollaboration()
    {
        // A set is only meaningful with its scope, and the case that proves it is a COLLISION: this
        // bundle is running its own live `agent-1` while a foreign set names one too. Matching them up
        // would report another conversation's agent as an agent this process is running — the guard
        // inside MarkInvalidated cannot catch that, because rebinding never reaches it.
        var bundle = CreateBundle();
        var root = RegisterRoot(bundle);
        bundle.Directory.TryRegister(
            root.CreateChild("agent-1", AgentKind.SubAgent, "writer", "writes things"),
            "writer",
            AgentCollaborationStatuses.Running
        );

        var report = AgentCollaborationRestartReconciler.Reconcile(
            bundle,
            Binding([Node("agent-1", "reviewer", collaborationId: "conv-b")], collaborationId: "conv-b")
        );

        report.IsEmpty.Should().BeTrue();
        bundle.Directory.Resolve("reviewer").FailureCode.Should().Be(AgentDirectoryFailureCodes.NotFound);
    }

    [Theory]
    [InlineData(AgentCollaborationStatuses.Queued)]
    [InlineData(AgentCollaborationStatuses.Running)]
    [InlineData(AgentCollaborationStatuses.Completed)]
    [InlineData(AgentCollaborationStatuses.Error)]
    [InlineData(AgentCollaborationStatuses.Stopped)]
    public void Reconcile_InvalidatesEveryLifecycleStatusAlike(string status)
    {
        // The status a row was written with says what the agent was doing when the process died, not
        // whether it survived. Nothing survived. A `queued` agent never started, a `running` one was
        // killed mid-turn, a `completed` one is finished — all three are equally unreachable now, and
        // treating any of them as still addressable would let a send be accepted for a dead endpoint.
        var bundle = CreateBundle();
        RegisterRoot(bundle);

        var report = AgentCollaborationRestartReconciler.Reconcile(
            bundle,
            Binding([Node("agent-1", "reviewer", status: status)])
        );

        report.Invalidated.Should().Equal("agent-1");
        bundle.Directory.Resolve("agent-1").FailureCode.Should().Be(AgentDirectoryFailureCodes.TargetNotLive);
    }

    [Fact]
    public void Reconcile_ReportsAnObligationOwedByAnInvalidatedAgent()
    {
        var bundle = CreateBundle();
        RegisterRoot(bundle);

        var report = AgentCollaborationRestartReconciler.Reconcile(
            bundle,
            Binding([Node("agent-1", "reviewer")], [Obligation("agent-1")])
        );

        report.AbandonedObligations.Should().Equal("agentmsg-agent-1");
    }

    [Fact]
    public void Reconcile_DoesNotReportAnObligationOwedByAnAgentThatIsStillLive()
    {
        // Only an obligation whose target did not survive is abandoned. One owed by an agent this
        // process is running is still owed, and reporting it as dead would be a false bereavement.
        var bundle = CreateBundle();
        RegisterRoot(bundle);

        var report = AgentCollaborationRestartReconciler.Reconcile(
            bundle,
            Binding([Node(RootAgentId, "root")], [Obligation(RootAgentId, fromAgentId: "agent-1")])
        );

        report.AbandonedObligations.Should().BeEmpty();
    }

    [Fact]
    public void Reconcile_IsIdempotent()
    {
        // Reconciliation is wired to a hydration path that can run more than once over one process.
        // A second pass must report nothing, or an operator would read a repeat as a fresh casualty.
        var bundle = CreateBundle();
        RegisterRoot(bundle);
        var set = Binding([Node("agent-1", "reviewer")], [Obligation("agent-1")]);

        _ = AgentCollaborationRestartReconciler.Reconcile(bundle, set);
        var second = AgentCollaborationRestartReconciler.Reconcile(bundle, set);

        second.Invalidated.Should().BeEmpty();
        second.AbandonedObligations.Should().BeEmpty();
    }

    [Fact]
    public void Reconcile_KeepsTwoRootsApart()
    {
        // Since #705 an agent id is an ordinal minted per root, so both bundles below really do hold an
        // `agent-1`. Each must resolve its own: without the collaboration id in the key, one root's
        // reconciliation would tombstone the other root's live agent.
        var a = CreateBundle("conv-a");
        var b = CreateBundle("conv-b");
        var rootB = AgentCollaborationContext.ForRoot("conv-b", RootAgentId);
        b.Directory.TryRegister(rootB, "root", AgentCollaborationStatuses.Running);
        b.Directory.TryRegister(
            rootB.CreateChild("agent-1", AgentKind.SubAgent, "writer", "writes things"),
            "writer",
            AgentCollaborationStatuses.Running
        );

        _ = AgentCollaborationRestartReconciler.Reconcile(
            a,
            Binding([Node("agent-1", "reviewer")], collaborationId: "conv-a")
        );

        b.Directory.Resolve("agent-1").Succeeded.Should().BeTrue("conv-b's agent-1 is live and is not conv-a's");
        b.Directory.Resolve("writer").Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Reconcile_IgnoresAnEmptySet()
    {
        var bundle = CreateBundle();
        RegisterRoot(bundle);

        AgentCollaborationRestartReconciler.Reconcile(bundle, Binding([])).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ToOperatorTrace_NamesEveryBucketOnOneLine()
    {
        var report = new RestartReconciliationReport(
            Rebound: ["agent-root"],
            Invalidated: ["agent-1", "agent-2"],
            AbandonedObligations: ["agentmsg-1"]
        );

        var trace = report.ToOperatorTrace();

        trace.Should().Contain("rebound 1");
        trace.Should().Contain("agent-root");
        trace.Should().Contain("not live 2");
        trace.Should().Contain("agent-1");
        trace.Should().Contain("agent-2");
        trace.Should().Contain("abandoned 1");
        trace.Should().Contain("agentmsg-1");
        trace.Should().NotContain("\n", "an operator trace that spans lines is one grep cannot pull whole");
    }

    [Fact]
    public void ToOperatorTrace_SaysNothingHappenedRatherThanNothingAtAll()
    {
        // An empty string in a log reads as a broken log line. "nothing to reconcile" is a fact.
        RestartReconciliationReport.Empty.ToOperatorTrace().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Reconcile_DeliversNothingToTheRoot()
    {
        // The load-bearing guarantee of the whole hydration path. The root has a live write endpoint,
        // and delivering into it enqueues on the loop's input channel — which STARTS A TURN. A restart
        // that woke the model to announce its own restart would burn a model run before the user had
        // typed anything, on every single hydration.
        var bundle = CreateBundle();
        var root = AgentCollaborationContext.ForRoot(RootId, RootAgentId);
        var endpoint = new RecordingEndpoint();
        bundle.Directory.TryRegister(root, "root", AgentCollaborationStatuses.Running, endpoint);

        var report = AgentCollaborationRestartReconciler.Reconcile(
            bundle,
            Binding([Node("agent-1", "reviewer")], [Obligation("agent-1")])
        );

        // Non-vacuity: the guarantee is an ABSENCE, so it is worth nothing unless this reconciliation
        // actually had an obligation owed to a dead agent to be tempted by.
        //
        // What this test does NOT prove, measured rather than assumed: adding
        // `NotifyAbandonedObligationsAsync(abandoned, dead)` to the reconciler leaves all 16 tests
        // green. That call is inert here because its first guard is `Ledger.Find`, and a restarted
        // process has a fresh ledger that has never heard of a persisted message id. So this asserts
        // that the reconciler as written delivers nothing — not that a delivery attempt would be
        // caught. The reason the call is omitted is in Reconcile's remarks, and it is not "it does
        // nothing": it is that the day it stopped doing nothing, it would start a root turn.
        report.AbandonedObligations.Should().ContainSingle();
        await Task.Yield();
        endpoint.Delivered.Should().BeEmpty();
    }

    private sealed class RecordingEndpoint : IAgentWriteEndpoint
    {
        public List<string> Delivered { get; } = [];

        public ValueTask<AgentDeliveryOutcome> DeliverAsync(
            AgentMessage message,
            CancellationToken cancellationToken = default
        )
        {
            Delivered.Add(message.MessageId);
            return ValueTask.FromResult(new AgentDeliveryOutcome(AgentDeliveryDisposition.Delivered));
        }
    }
}
