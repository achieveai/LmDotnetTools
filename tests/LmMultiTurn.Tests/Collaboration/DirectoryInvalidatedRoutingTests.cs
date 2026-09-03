using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// What the directory does with an agent that a previous process spawned and this one cannot reach: it
/// is a tombstone, not a registration. Resolving one says <c>target_not_live</c> — a real agent whose
/// process is gone — rather than <c>not_found</c>, which would read as "you made that name up".
/// </summary>
public class DirectoryInvalidatedRoutingTests
{
    private const string CollaborationId = "collab-1";

    private static AgentCollaborationDirectory CreateDirectory() =>
        new(CollaborationId, new AgentCollaborationOptions());

    private static AgentCollaborationContext RegisterRoot(AgentCollaborationDirectory directory)
    {
        var root = AgentCollaborationContext.ForRoot(CollaborationId, "agent-root");
        directory.TryRegister(root, "root", AgentCollaborationStatuses.Running).Succeeded.Should().BeTrue();
        return root;
    }

    private static CollaborationNodeRecord Row(string agentId, string name, string collaborationId = CollaborationId) =>
        new()
        {
            AgentId = agentId,
            CollaborationId = collaborationId,
            Name = name,
            ParentAgentId = "agent-root",
            AncestorAgentIds = ["agent-root"],
            Kind = AgentKind.SubAgent,
            Role = "reviewer",
            Description = "reviews diffs",
            StructuralDepth = 1,
            DelegationDepth = 1,
            Status = AgentCollaborationStatuses.Running,
        };

    [Fact]
    public void Resolve_ReportsTargetNotLive_ForAnInvalidatedName()
    {
        var directory = CreateDirectory();
        RegisterRoot(directory);
        directory.MarkInvalidated(Row("agent-1", "reviewer"));

        var resolution = directory.Resolve("reviewer");

        resolution.Succeeded.Should().BeFalse();
        resolution.FailureCode.Should().Be(AgentDirectoryFailureCodes.TargetNotLive);
    }

    [Fact]
    public void Resolve_ReportsTargetNotLive_ForAnInvalidatedAgentId()
    {
        var directory = CreateDirectory();
        RegisterRoot(directory);
        directory.MarkInvalidated(Row("agent-1", "reviewer"));

        directory.Resolve("agent-1").FailureCode.Should().Be(AgentDirectoryFailureCodes.TargetNotLive);
    }

    [Fact]
    public void Resolve_StillReportsNotFound_ForANameNobodyEverHeld()
    {
        // The whole value of the new code is that it is DIFFERENT from not_found. A tombstone map that
        // swallowed every miss would tell a model to "spawn it again" for a name it simply mistyped.
        var directory = CreateDirectory();
        RegisterRoot(directory);
        directory.MarkInvalidated(Row("agent-1", "reviewer"));

        directory.Resolve("nobody").FailureCode.Should().Be(AgentDirectoryFailureCodes.NotFound);
    }

    [Fact]
    public void Resolve_PrefersALiveRegistration_OverATombstoneOfTheSameName()
    {
        // A re-spawn under a name a dead agent held must reach the live agent. Ordering in Resolve is
        // the single mechanism for this — there is deliberately no second guard inside MarkInvalidated,
        // because two guards for one rule make each other's mutations invisible.
        var directory = CreateDirectory();
        var root = RegisterRoot(directory);
        directory.MarkInvalidated(Row("agent-1", "reviewer"));

        var respawn = root.CreateChild("agent-2", AgentKind.SubAgent, "reviewer", "reviews diffs");
        directory
            .TryRegister(respawn, "reviewer", AgentCollaborationStatuses.Running)
            .Succeeded.Should()
            .BeTrue("a tombstone must not block re-spawning under the same name");

        var resolution = directory.Resolve("reviewer");
        resolution.Succeeded.Should().BeTrue();
        resolution.Entry!.AgentId.Should().Be("agent-2");
    }

    [Fact]
    public void Snapshot_DoesNotListATombstone()
    {
        // GetAgents reads Snapshot. A tombstone is resolvable so a sender can be told what became of an
        // agent; it is not a member of the collaboration and must never be offered as one to talk to.
        var directory = CreateDirectory();
        RegisterRoot(directory);
        directory.MarkInvalidated(Row("agent-1", "reviewer"));

        directory.Snapshot().Should().ContainSingle().Which.AgentId.Should().Be("agent-root");
    }

    [Fact]
    public void MarkInvalidated_IgnoresARowFromAnotherCollaboration()
    {
        // (scope, agent id), not agent id. Since #705 every conversation has an `agent-1`; a row from
        // another root names a real agent that is none of this directory's business, and tombstoning it
        // here would refuse a name this collaboration may legitimately be about to use.
        var directory = CreateDirectory();
        RegisterRoot(directory);

        directory.MarkInvalidated(Row("agent-1", "reviewer", collaborationId: "collab-other")).Should().BeFalse();

        directory.Resolve("reviewer").FailureCode.Should().Be(AgentDirectoryFailureCodes.NotFound);
    }

    [Fact]
    public void MarkInvalidated_IsIdempotent()
    {
        // Reconciliation may run more than once over the same persisted set; the second pass must not
        // report fresh work, or an operator trace would grow on every hydration.
        var directory = CreateDirectory();
        RegisterRoot(directory);

        directory.MarkInvalidated(Row("agent-1", "reviewer")).Should().BeTrue();
        directory.MarkInvalidated(Row("agent-1", "reviewer")).Should().BeFalse();
    }

    [Fact]
    public void Resolve_ReportsAmbiguity_WhenTwoTombstonesShareAName()
    {
        // Same rule as a live name: once two agents have answered to a name, a sender cannot know which
        // it meant, and answering "here is one of them" would be worse than refusing.
        var directory = CreateDirectory();
        RegisterRoot(directory);
        directory.MarkInvalidated(Row("agent-1", "reviewer"));
        directory.MarkInvalidated(Row("agent-2", "reviewer"));

        directory.Resolve("reviewer").FailureCode.Should().Be(AgentDirectoryFailureCodes.AmbiguousName);
        directory
            .Resolve("agent-1")
            .FailureCode.Should()
            .Be(AgentDirectoryFailureCodes.TargetNotLive, "the ids stay individually resolvable");
    }

    [Fact]
    public void InvalidatedAgentId_NamesTheAgentBehindATombstonedTarget()
    {
        // #672: a caller recording ownership needs the identifier, and Resolve deliberately cannot carry
        // it — a tombstone answers with a null entry. Both spellings of the same agent must give the
        // same identifier, or the agent is keyed one way before a restart and another way after one.
        var directory = CreateDirectory();
        RegisterRoot(directory);
        directory.MarkInvalidated(Row("agent-1", "reviewer"));

        directory.InvalidatedAgentId("reviewer").Should().Be("agent-1");
        directory.InvalidatedAgentId("agent-1").Should().Be("agent-1");
    }

    [Fact]
    public void InvalidatedAgentId_RefusesToGuess_WhenTwoTombstonesShareAName()
    {
        // The ambiguity rule follows the identifier, not just the resolution: naming either agent here
        // would hand one of them ownership of the other's work.
        var directory = CreateDirectory();
        RegisterRoot(directory);
        directory.MarkInvalidated(Row("agent-1", "reviewer"));
        directory.MarkInvalidated(Row("agent-2", "reviewer"));

        directory.InvalidatedAgentId("reviewer").Should().BeNull();
        directory.InvalidatedAgentId("agent-2").Should().Be("agent-2", "the ids stay individually nameable");
    }

    [Fact]
    public void InvalidatedAgentId_IsNullForATargetNoTombstoneHolds()
    {
        var directory = CreateDirectory();
        RegisterRoot(directory);
        directory.MarkInvalidated(Row("agent-1", "reviewer"));

        directory.InvalidatedAgentId("nobody").Should().BeNull();
        directory.InvalidatedAgentId("agent-root").Should().BeNull("a live agent is not a tombstone");
    }

    [Fact]
    public void OnDirectoryChanged_FiresForEveryWriteThatChangesWhoIsReachable()
    {
        var directory = CreateDirectory();
        var changes = 0;
        directory.OnDirectoryChanged += () => changes++;

        var root = RegisterRoot(directory);
        directory.TryRegister(
            root.CreateChild("agent-1", AgentKind.SubAgent, "reviewer", "reviews diffs"),
            "reviewer",
            AgentCollaborationStatuses.Queued
        );
        directory.TryUpdateStatus("agent-1", AgentCollaborationStatuses.Running);
        directory.TryMarkRetained("agent-1");
        directory.MarkInvalidated(Row("agent-9", "gone"));

        changes.Should().Be(5);
    }

    [Fact]
    public void OnDirectoryChanged_DoesNotFireForAWriteThatChangedNothing()
    {
        var directory = CreateDirectory();
        RegisterRoot(directory);
        var changes = 0;
        directory.OnDirectoryChanged += () => changes++;

        directory.TryUpdateStatus("agent-missing", AgentCollaborationStatuses.Running).Should().BeFalse();
        directory.TryRegister(
            AgentCollaborationContext.ForRoot(CollaborationId, "agent-root"),
            "root",
            AgentCollaborationStatuses.Running
        );

        changes.Should().Be(0);
    }

    [Fact]
    public void OnDirectoryChanged_KeepsGoingWhenOneSubscriberThrows()
    {
        // The durable binding write and any live push share this multicast. A plain invoke aborts the
        // rest of the invocation list on the first throw, which would let a broken push cost durability.
        var directory = CreateDirectory();
        var reached = false;
        directory.OnDirectoryChanged += () => throw new InvalidOperationException("subscriber is broken");
        directory.OnDirectoryChanged += () => reached = true;

        var register = () => RegisterRoot(directory);

        register.Should().NotThrow();
        reached.Should().BeTrue();
    }

    [Fact]
    public void SnapshotRecords_CarriesTheAdmissionInstant()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));
        var directory = new AgentCollaborationDirectory(CollaborationId, new AgentCollaborationOptions(), clock);
        RegisterRoot(directory);

        directory.SnapshotRecords().Should().ContainSingle().Which.SpawnedAt.Should().Be(clock.GetUtcNow());
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
