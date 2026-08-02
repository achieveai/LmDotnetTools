using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// Covers what <see cref="AgentCollaborationBundle.TrySendAndDeliver"/> must still promise when its
/// target retires while senders are admitting messages to it.
/// </summary>
/// <remarks>
/// <see cref="AgentCollaborationBundle.RetireAgent"/> runs its one-time abandon sweep without taking
/// the bundle's own delivery gate — the lock that makes admission atomic for
/// <see cref="AgentCollaborationBundle.TrySendAndDeliver"/>. That leaves a real window: a sender can
/// read the target's directory entry as live, then retirement can run its entire sweep (finding
/// nothing to abandon, because the message is not admitted yet), and only afterwards does the
/// sender's admission land in the ledger — a reply-expecting message left "Accepted" forever, since
/// the sweep that would have closed it already ran and will never run again.
///
/// The race is aligned with a <see cref="Barrier"/> rather than paced with sleeps, matching
/// <c>AgentCollaborationDirectoryConcurrencyTests</c> and <c>AgentMessageLedgerConcurrencyTests</c>:
/// several sender threads hammer admissions concurrently with one retirement, repeated over many
/// attempts, so a scheduler that happens to serialise one attempt still gets many chances to land
/// inside the (small) vulnerable window. No sleeps, no production test hooks.
/// </remarks>
public class AgentCollaborationBundleConcurrencyTests
{
    private const string CollaborationId = "collab-1";
    private const int SenderThreads = 8;
    private const int AdmissionAttemptsPerThread = 3_000;
    private const int Attempts = 150;

    private static AgentCollaborationBundle CreateBundle()
    {
        return new AgentCollaborationBundle(CollaborationId, new AgentCollaborationOptions());
    }

    private static AgentCollaborationContext Populate(AgentCollaborationBundle bundle)
    {
        var root = AgentCollaborationContext.ForRoot(CollaborationId, "agent-root");
        _ = bundle.Directory.TryRegister(root, "root", "running");
        return root;
    }

    private static void AddChild(
        AgentCollaborationBundle bundle,
        AgentCollaborationContext parent,
        string agentId,
        string name
    )
    {
        var child = parent.CreateChild(agentId, AgentKind.SubAgent, name, $"{name} description");
        _ = bundle.Directory.TryRegister(child, name, "running");
    }

    /// <summary>
    /// Runs <paramref name="body"/> on <paramref name="participants"/> dedicated threads that are all
    /// released from a barrier at the same instant. Kept out of the test method itself so the blocking
    /// join lives in a helper, not a <c>[Fact]</c>.
    /// </summary>
    private static void Race(int participants, Action<int> body)
    {
        using var barrier = new Barrier(participants);
        var threads = Enumerable
            .Range(0, participants)
            .Select(index =>
                Task.Factory.StartNew(
                    () =>
                    {
                        barrier.SignalAndWait();
                        body(index);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                )
            )
            .ToArray();

        Task.WaitAll(threads);
    }

    [Fact]
    public void TrySendAndDeliver_RacingRetireAgent_NeverLeavesAQuestionOpenPastTheOneTimeSweep()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var bundle = CreateBundle();
            var root = Populate(bundle);
            AddChild(bundle, root, "agent-a", "reviewer");

            var admitted = new ConcurrentBag<string>();

            // Participants 0..SenderThreads-1 hammer admissions; the last participant retires the
            // target exactly once. Several concurrent senders (rather than just one) raise the odds
            // that some admission's "read live, then admit" straddles the retirement's sweep.
            Race(
                SenderThreads + 1,
                index =>
                {
                    if (index == SenderThreads)
                    {
                        _ = bundle.RetireAgent("agent-a", "stopped");
                        return;
                    }

                    for (var i = 0; i < AdmissionAttemptsPerThread; i++)
                    {
                        var dispatch = bundle.TrySendAndDeliver(
                            "agent-root",
                            "reviewer",
                            AgentMessageType.Question,
                            null,
                            (deliveredMessageId, targetAgentId) =>
                            {
                                // Mirrors AgentCollaborationMessenger.DeliverAsync's own finally
                                // block: free the inbox slot without touching the ledger, so the
                                // loop is never throttled by inbox capacity and every admitted
                                // entry is left exactly where an unswept admission would leave it
                                // -- Accepted, nothing else.
                                _ = deliveredMessageId;
                                _ = bundle.Directory.GetInbox(targetAgentId)?.TryDequeue(out _);
                                return Task.CompletedTask;
                            }
                        );

                        if (dispatch.Result.Succeeded)
                        {
                            admitted.Add(dispatch.Result.MessageId!);
                        }
                    }
                }
            );

            // Nothing in this test ever answers, delivers, or fails a message, so the only way any
            // admitted entry can be closed is the retirement's own sweep. Every admission a sender
            // saw as accepted must therefore be closed by now -- an open one proves it was admitted
            // after the one-time sweep had already run, and so will never be closed by anything.
            foreach (var messageId in admitted)
            {
                bundle
                    .Ledger.Find(messageId)!
                    .IsClosed.Should()
                    .BeTrue(
                        $"message {messageId} was admitted for 'agent-a' and must not survive "
                            + "its concurrent retirement unclosed"
                    );
            }

            bundle.Ledger.GetOpenInbound("agent-a").Should().BeEmpty();
        }
    }
}
