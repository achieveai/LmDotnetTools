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

        // Admission only claims the question. Closing it here would tell the asker it had been
        // answered before the answer had reached it, and a delivery that then failed could never be
        // retried.
        bundle.Ledger.Find(question)!.State.Should().Be(AgentMessageDeliveryState.Accepted);
        bundle.Ledger.GetOpenOutbound("agent-root").Should().NotBeEmpty();

        _ = bundle.Ledger.MarkDelivered(answer.MessageId!);

        bundle.Ledger.Find(question)!.State.Should().Be(AgentMessageDeliveryState.Answered);
        bundle.Ledger.GetOpenOutbound("agent-root").Should().BeEmpty();
    }

    [Fact]
    public void TrySend_ToAQueuedTarget_IsRefusedAtAdmissionRatherThanAfterAcceptance()
    {
        var bundle = CreateBundle();
        var root = Populate(bundle);
        var child = root.CreateChild("agent-a", AgentKind.SubAgent, "reviewer", "reviews things");
        _ = bundle.Directory.TryRegister(child, "reviewer", AgentCollaborationStatuses.Queued);

        var result = bundle.TrySend("agent-root", "reviewer", AgentMessageType.Question);

        // A queued agent is resolvable and has an inbox, but no turn to inject into. Admitting here
        // would tell the sender "accepted" and then have the target's owner refuse the hand-off, which
        // the sender never sees. Refusing now keeps admission and delivery agreeing, and the code is
        // recoverable: the same message may be sent once the target reports running.
        result.FailureCode.Should().Be(AgentMessageFailureCodes.TargetNotStarted);
        result.Target!.AgentId.Should().Be("agent-a");
        bundle.Ledger.Count.Should().Be(0);

        _ = bundle.Directory.TryUpdateStatus("agent-a", AgentCollaborationStatuses.Running);
        bundle.TrySend("agent-root", "reviewer", AgentMessageType.Question)
            .Succeeded.Should()
            .BeTrue();
    }

    [Fact]
    public void TrySend_SteerToAnAgentThatIsNotRunning_IsRefusedWhileAnythingElseIsAdmitted()
    {
        var bundle = CreateBundle();
        var root = Populate(bundle);
        _ = AddChild(bundle, root, "agent-a", "reviewer");
        _ = bundle.Directory.TryUpdateStatus("agent-a", AgentCollaborationStatuses.Completed);

        // Only the steer is refused. The agent is still addressable — a question restarts it — so a
        // blanket refusal would be wrong; what has no meaning is redirecting work that is not running.
        bundle
            .TrySend("agent-root", "reviewer", AgentMessageType.Steer)
            .FailureCode.Should()
            .Be(AgentMessageFailureCodes.TargetNotActive);
        bundle
            .TrySend("agent-root", "reviewer", AgentMessageType.Question)
            .Succeeded.Should()
            .BeTrue();
    }

    [Fact]
    public async Task TrySendAndDeliver_HandsMessagesToOneTargetOverInAdmissionOrder()
    {
        var bundle = CreateBundle();
        var root = Populate(bundle);
        _ = AddChild(bundle, root, "agent-a", "reviewer");

        var handedOver = new List<string>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The first delivery is held open, so the second is admitted while the first is still in
        // flight. Without a per-target chain the second would overtake it and the target would see a
        // reply before the message it replies to.
        var first = bundle.TrySendAndDeliver(
            "agent-root",
            "reviewer",
            AgentMessageType.Question,
            null,
            async (messageId, _) =>
            {
                await gate.Task;
                lock (handedOver)
                {
                    handedOver.Add(messageId);
                }
            }
        );

        var second = bundle.TrySendAndDeliver(
            "agent-root",
            "reviewer",
            AgentMessageType.Question,
            null,
            (messageId, _) =>
            {
                lock (handedOver)
                {
                    handedOver.Add(messageId);
                }

                return Task.CompletedTask;
            }
        );

        second.Result.Succeeded.Should().BeTrue();
        handedOver.Should().BeEmpty(because: "the second must not overtake the first");

        gate.SetResult();
        await second.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        handedOver.Should().Equal(first.Result.MessageId!, second.Result.MessageId!);
    }

    [Fact]
    public async Task TrySendAndDeliver_KeepsTheChainRunning_WhenOneDeliveryThrows()
    {
        var bundle = CreateBundle();
        var root = Populate(bundle);
        _ = AddChild(bundle, root, "agent-a", "reviewer");

        var faulted = bundle.TrySendAndDeliver(
            "agent-root",
            "reviewer",
            AgentMessageType.Question,
            null,
            (_, _) => Task.FromException(new InvalidOperationException("boom"))
        );

        var reached = false;
        var next = bundle.TrySendAndDeliver(
            "agent-root",
            "reviewer",
            AgentMessageType.Question,
            null,
            (_, _) =>
            {
                reached = true;
                return Task.CompletedTask;
            }
        );

        // A delivery records its own outcome in the ledger, so letting its fault travel down the chain
        // would cancel deliveries that have nothing to do with it.
        await next.Delivery.WaitAsync(TimeSpan.FromSeconds(10));
        reached.Should().BeTrue();
        faulted.Result.Succeeded.Should().BeTrue();
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
    public void RetireAgent_ClosesTheQuestionsTheDepartingAgentAsked_SoNobodyAnswersAGhost()
    {
        var bundle = CreateBundle();
        var root = Populate(bundle);
        var asker = AddChild(bundle, root, "agent-a", "reviewer");
        _ = AddChild(bundle, root, "agent-b", "builder");
        var question = bundle.TrySend("agent-a", "builder", AgentMessageType.Question).MessageId!;

        var closed = bundle.RetireAgent("agent-a", "stopped");

        // The other half of leaving. Left open, the builder is still offered this as answerable work —
        // it can spend a wait interrupt on it and write a reply — for an asker that has gone.
        closed.Should().Equal(question);
        bundle.Ledger.Find(question)!.State.Should().Be(AgentMessageDeliveryState.Abandoned);
        bundle
            .Ledger.Find(question)!
            .ReasonCode.Should()
            .Be(AgentCollaborationBundle.SenderLeftReasonCode);
        bundle.Ledger.GetOpenInbound("agent-b").Should().BeEmpty();
        bundle
            .Ledger.TryAdmit(
                new AgentMessageAdmissionRequest(
                    "agent-b",
                    asker.AgentId,
                    AgentMessageType.Response,
                    question
                ),
                new AgentInbox(8)
            )
            .FailureCode.Should()
            .Be(AgentMessageFailureCodes.CorrelationClosed);
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
