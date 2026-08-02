using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// Covers the two orderings the bundle exists to write down once: resolve-then-admit for sending, and
/// look-up-then-apply-policy for reading — plus the single call that retires an agent without stranding
/// anyone who was waiting on it.
/// </summary>
/// <remarks>
/// These are composition tests. The directory and ledger are each correct alone; what is asserted here
/// is that they are combined in the one order that is safe — a resolved-but-departed agent is refused
/// before anything is queued for it, and leaving closes the correspondence rather than orphaning it.
/// </remarks>
public class AgentCollaborationBundleTests
{
    private const string CollaborationId = "collab-1";

    private static AgentCollaborationBundle CreateBundle(AgentCollaborationOptions? options = null)
    {
        return new AgentCollaborationBundle(
            CollaborationId,
            options ?? new AgentCollaborationOptions()
        );
    }

    private static AgentCollaborationContext Populate(AgentCollaborationBundle bundle)
    {
        var root = AgentCollaborationContext.ForRoot(CollaborationId, "agent-root");
        _ = bundle.Directory.TryRegister(root, "root", "running");
        return root;
    }

    private static AgentCollaborationContext AddChild(
        AgentCollaborationBundle bundle,
        AgentCollaborationContext parent,
        string agentId,
        string name
    )
    {
        var child = parent.CreateChild(agentId, AgentKind.SubAgent, name, $"{name} description");
        _ = bundle.Directory.TryRegister(child, name, "running");
        return child;
    }

    [Fact]
    public void TrySend_ResolvesByName_AndReportsTheAgentItReached()
    {
        var bundle = CreateBundle();
        var root = Populate(bundle);
        _ = AddChild(bundle, root, "agent-a", "reviewer");

        var result = bundle.TrySend("agent-root", "reviewer", AgentMessageType.Question);

        // The name is what a model has; the resolved entry is what the caller needs back so it can
        // report and correlate against a stable identity.
        result.Succeeded.Should().BeTrue();
        result.Target!.AgentId.Should().Be("agent-a");
        bundle.Ledger.GetOpenInbound("agent-a").Should().ContainSingle();
    }

    [Fact]
    public void TrySend_ReachesAcrossBranches_NotJustDownOne()
    {
        var bundle = CreateBundle();
        var root = Populate(bundle);
        var left = AddChild(bundle, root, "agent-a", "reviewer");
        _ = AddChild(bundle, root, "agent-b", "tester");

        // The whole point of a root-owned directory: neither of these agents' own managers knows the
        // other exists, so without it this send has nowhere to look.
        bundle
            .TrySend(left.AgentId, "tester", AgentMessageType.Question)
            .Succeeded.Should()
            .BeTrue();
    }

    [Fact]
    public void TrySend_ReportsTheDirectoryRefusal_WhenTheTargetCannotBeResolved()
    {
        var bundle = CreateBundle();
        _ = Populate(bundle);

        var result = bundle.TrySend("agent-root", "nobody", AgentMessageType.Question);

        result.FailureCode.Should().Be(AgentDirectoryFailureCodes.NotFound);
        result.Target.Should().BeNull();
        bundle.Ledger.Count.Should().Be(0);
    }

    [Fact]
    public void TrySend_RefusesAnAgentThatHasLeft_ButStillSaysWhoItWas()
    {
        var bundle = CreateBundle();
        var root = Populate(bundle);
        _ = AddChild(bundle, root, "agent-a", "reviewer");
        _ = bundle.RetireAgent("agent-a", "completed");

        var result = bundle.TrySend("agent-root", "reviewer", AgentMessageType.Question);

        // Refused, but not anonymous: the sender learns the agent existed and finished, which is a
        // different situation from a name that never meant anything.
        result.FailureCode.Should().Be(AgentMessageFailureCodes.UnknownTarget);
        result.Target!.Status.Should().Be("completed");
        bundle.Ledger.Count.Should().Be(0);
    }

    [Fact]
    public void TrySend_ReportsTheLedgerRefusal_WhenAdmissionIsDeclined()
    {
        var bundle = CreateBundle(new AgentCollaborationOptions { MaxInboxMessages = 1 });
        var root = Populate(bundle);
        _ = AddChild(bundle, root, "agent-a", "reviewer");
        _ = bundle.TrySend("agent-root", "reviewer", AgentMessageType.Steer);

        // One code path, two sources of refusal: the caller does not have to know whether it was the
        // directory or the ledger that said no.
        bundle
            .TrySend("agent-root", "reviewer", AgentMessageType.Steer)
            .FailureCode.Should()
            .Be(AgentMessageFailureCodes.InboxFull);
    }

    [Fact]
    public void TrySend_CarriesCorrelationThrough_SoAReplyClosesItsQuestion()
    {
        var bundle = CreateBundle();
        var root = Populate(bundle);
        _ = AddChild(bundle, root, "agent-a", "reviewer");
        var question = bundle
            .TrySend("agent-root", "reviewer", AgentMessageType.Question)
            .MessageId!;

        var answer = bundle.TrySend("agent-a", "root", AgentMessageType.Response, question);

        answer.Succeeded.Should().BeTrue();
        bundle.Ledger.Find(question)!.State.Should().Be(AgentMessageDeliveryState.Answered);
        bundle.Ledger.GetOpenOutbound("agent-root").Should().BeEmpty();
    }

    [Fact]
    public void EvaluateTranscriptAccess_AppliesTheConfiguredMode()
    {
        var restricted = CreateBundle();
        var open = CreateBundle(
            new AgentCollaborationOptions { TranscriptVisibility = TranscriptVisibilityMode.Open }
        );

        foreach (var bundle in new[] { restricted, open })
        {
            var root = Populate(bundle);
            _ = AddChild(bundle, root, "agent-a", "reviewer");
            _ = AddChild(bundle, root, "agent-b", "tester");
        }

        restricted.EvaluateTranscriptAccess("agent-a", "agent-b").IsAllowed.Should().BeFalse();
        open.EvaluateTranscriptAccess("agent-a", "agent-b").IsAllowed.Should().BeTrue();
        // Ancestry is allowed under either mode, so widening never has to be turned on to make the
        // ordinary parent-reads-child case work.
        restricted.EvaluateTranscriptAccess("agent-root", "agent-a").IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void EvaluateTranscriptAccess_ResolvesTheTargetByNameToo()
    {
        var bundle = CreateBundle();
        var root = Populate(bundle);
        _ = AddChild(bundle, root, "agent-a", "reviewer");

        // A reader addresses a target the same way it would to send to it; requiring a canonical
        // identifier only for reads would be a trap.
        bundle.EvaluateTranscriptAccess("agent-root", "reviewer").IsAllowed.Should().BeTrue();
        bundle
            .EvaluateTranscriptAccess("agent-root", "nobody")
            .Reason.Should()
            .Be(TranscriptAccessReasons.UnknownTarget);
    }

    [Fact]
    public void RetireAgent_ClosesOutstandingMessages_AndKeepsTheEntryReadable()
    {
        var bundle = CreateBundle();
        var root = Populate(bundle);
        _ = AddChild(bundle, root, "agent-a", "reviewer");
        var question = bundle
            .TrySend("agent-root", "reviewer", AgentMessageType.Question)
            .MessageId!;

        var closed = bundle.RetireAgent("agent-a", "stopped");

        // Retiring and abandoning are one operation because either alone is wrong: the sender must be
        // released, and the departed agent must stay describable.
        closed.Should().Equal(question);
        bundle.Ledger.Find(question)!.State.Should().Be(AgentMessageDeliveryState.Abandoned);
        bundle
            .Ledger.Find(question)!
            .ReasonCode.Should()
            .Be(AgentCollaborationBundle.TargetLeftReasonCode);

        var entry = bundle.Directory.Resolve("agent-a").Entry!;
        entry.Status.Should().Be("stopped");
        entry.IsLive.Should().BeFalse();
    }

    [Fact]
    public void RetireAgent_IsHarmlessForAnAgentTheCollaborationNeverKnew()
    {
        var bundle = CreateBundle();
        _ = Populate(bundle);

        bundle.RetireAgent("agent-nobody", "stopped").Should().BeEmpty();
    }

    [Fact]
    public void Constructor_RejectsABlankCollaborationIdentity()
    {
        FluentActions
            .Invoking(() => new AgentCollaborationBundle("  ", new AgentCollaborationOptions()))
            .Should()
            .Throw<ArgumentException>();
    }
}
