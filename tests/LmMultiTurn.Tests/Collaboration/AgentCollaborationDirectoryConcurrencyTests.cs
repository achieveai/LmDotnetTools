using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// Covers what the directory must still promise when several spawns land at once: an identifier is
/// admitted exactly once, a refused registration leaves nothing addressable behind, and a name
/// contested by concurrent arrivals latches ambiguous instead of resolving to a guess.
/// </summary>
/// <remarks>
/// The directory keeps identity in one dictionary and name bindings in another, and a registration
/// writes to both. Nothing spans the two atomically, so the interesting question is not whether a
/// reader can observe the gap — it can — but whether the collaboration is left *consistent* once the
/// racing spawns have finished. These tests therefore assert at quiescence: after every racer has
/// returned, identifiers, names, inboxes and counts must all agree on the same set of agents.
///
/// The races are aligned with a <see cref="Barrier"/> rather than paced with sleeps, so contention
/// comes from a rendezvous that either happens or blocks, never from a timing guess that can pass on
/// a fast machine and fail on a loaded one. Each race is repeated so that a scheduler which happens
/// to serialise one attempt still gets many chances to interleave.
/// </remarks>
public class AgentCollaborationDirectoryConcurrencyTests
{
    private const string CollaborationId = "collab-1";
    private const int Racers = 16;
    private const int Attempts = 8;

    private static AgentCollaborationDirectory CreateDirectory(AgentCollaborationOptions? options = null)
    {
        return new AgentCollaborationDirectory(
            CollaborationId,
            options ?? new AgentCollaborationOptions { MaxTotalAgents = 256 }
        );
    }

    private static AgentCollaborationContext RegisterRoot(AgentCollaborationDirectory directory)
    {
        var root = AgentCollaborationContext.ForRoot(CollaborationId, "agent-root");
        directory.TryRegister(root, "root", "running").Succeeded.Should().BeTrue();
        return root;
    }

    /// <summary>
    /// Runs <paramref name="body"/> on <paramref name="participants"/> dedicated threads that are all
    /// released from a barrier at the same instant.
    /// </summary>
    /// <remarks>
    /// The threads are <see cref="TaskCreationOptions.LongRunning"/> on purpose: a barrier whose
    /// participants are queued behind each other on a bounded thread pool would deadlock rather than
    /// contend, which is the opposite of what these tests need.
    /// </remarks>
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
    public void TryRegister_UnderConcurrency_AdmitsOneIdentifierExactlyOnce()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var directory = CreateDirectory();
            var root = RegisterRoot(directory);
            var contested = root.CreateChild("agent-a", AgentKind.SubAgent, "reviewer", "reviews");
            var results = new AgentRegistrationResult[Racers];

            // Every racer offers the same identifier under a different name, which is the shape a
            // retry storm or a double-spawn takes: one agent, many callers convinced they own it.
            Race(Racers, index => results[index] = directory.TryRegister(contested, $"name-{index}", "running"));

            results.Count(result => result.Succeeded).Should().Be(1);
            results
                .Where(result => !result.Succeeded)
                .Should()
                .OnlyContain(result =>
                    result.FailureCode == AgentDirectoryFailureCodes.DuplicateAgentId && result.Entry == null
                );
            directory.Count.Should().Be(2);
        }
    }

    [Fact]
    public void TryRegister_UnderConcurrency_BindsNoNameForARefusedRegistration()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var directory = CreateDirectory();
            var root = RegisterRoot(directory);
            var contested = root.CreateChild("agent-a", AgentKind.SubAgent, "reviewer", "reviews");
            var results = new AgentRegistrationResult[Racers];

            Race(Racers, index => results[index] = directory.TryRegister(contested, $"name-{index}", "running"));

            // The half-state that would matter is a name bound to an agent that was never admitted:
            // a later sender would resolve that name and address an agent the directory does not have.
            // Only the winner's name may exist, and it must point at the one admitted entry.
            var winner = Array.FindIndex(results, result => result.Succeeded);
            for (var index = 0; index < Racers; index++)
            {
                var resolution = directory.Resolve($"name-{index}");
                if (index == winner)
                {
                    resolution.Entry!.AgentId.Should().Be("agent-a");
                }
                else
                {
                    resolution.FailureCode.Should().Be(AgentDirectoryFailureCodes.NotFound);
                }
            }
        }
    }

    [Fact]
    public void TryRegister_UnderConcurrency_LeavesEveryDistinctAgentFullyAddressable()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var directory = CreateDirectory();
            var root = RegisterRoot(directory);
            var children = Enumerable
                .Range(0, Racers)
                .Select(index => root.CreateChild($"agent-{index}", AgentKind.SubAgent, "reviewer", "reviews"))
                .ToArray();
            var results = new AgentRegistrationResult[Racers];

            Race(Racers, index => results[index] = directory.TryRegister(children[index], $"name-{index}", "running"));

            results.Should().OnlyContain(result => result.Succeeded);
            directory.Count.Should().Be(Racers + 1);

            // Registration writes identity and the name binding separately. Once the racers have all
            // returned, both halves must be present for every agent — a lost name binding would leave
            // an agent that GetAgents lists but nobody can address by the name it was given.
            var snapshot = directory.Snapshot();
            snapshot.Select(entry => entry.AgentId).Should().OnlyHaveUniqueItems();
            for (var index = 0; index < Racers; index++)
            {
                directory.FindById($"agent-{index}").Should().NotBeNull();
                directory.Resolve($"name-{index}").Entry!.AgentId.Should().Be($"agent-{index}");
                directory.GetInbox($"agent-{index}").Should().NotBeNull();
            }
        }
    }

    [Fact]
    public void TryRegister_UnderConcurrency_LatchesAContestedNameRatherThanGuessing()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var directory = CreateDirectory();
            var root = RegisterRoot(directory);
            var children = Enumerable
                .Range(0, Racers)
                .Select(index => root.CreateChild($"agent-{index}", AgentKind.SubAgent, "reviewer", "reviews"))
                .ToArray();

            // Distinct identifiers, one shared name: every racer is admitted, but the name they all
            // claimed must end up owned by none of them.
            Race(
                Racers,
                index => directory.TryRegister(children[index], "reviewer", "running").Succeeded.Should().BeTrue()
            );

            directory.Count.Should().Be(Racers + 1);
            directory.Resolve("reviewer").FailureCode.Should().Be(AgentDirectoryFailureCodes.AmbiguousName);

            // Ambiguity must not cost the agents their identity: each is still reachable by the
            // canonical identifier that the name was only ever a convenience for.
            for (var index = 0; index < Racers; index++)
            {
                directory.Resolve($"agent-{index}").Entry!.AgentId.Should().Be($"agent-{index}");
            }
        }
    }

    [Fact]
    public void TryRegister_RacingRetirement_LeavesTheEntryConsistentRatherThanTornInHalf()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var directory = CreateDirectory();
            var root = RegisterRoot(directory);
            var children = Enumerable
                .Range(0, Racers)
                .Select(index => root.CreateChild($"agent-{index}", AgentKind.SubAgent, "reviewer", "reviews"))
                .ToArray();
            var observed = new ConcurrentBag<string>();

            // Half the racers admit an agent while the other half retire an already-admitted one, so
            // the compare-and-swap that rewrites an entry runs against live insertions.
            Race(
                Racers,
                index =>
                {
                    if (index % 2 == 0)
                    {
                        directory.TryRegister(children[index], $"name-{index}", "running").Succeeded.Should().BeTrue();
                    }
                    else if (directory.TryUpdateStatus("agent-root", "completed"))
                    {
                        observed.Add("updated");
                    }
                }
            );

            _ = directory.TryMarkRetained("agent-root");

            // A torn entry would show a rewritten status having lost the fields it did not touch, or a
            // retained flag applied to a stale copy. Every field must still describe the same agent.
            var rootEntry = directory.FindById("agent-root")!;
            rootEntry.AgentId.Should().Be("agent-root");
            rootEntry.CollaborationId.Should().Be(CollaborationId);
            rootEntry.Kind.Should().Be(AgentKind.Root);
            rootEntry.Status.Should().Be("completed");
            rootEntry.IsLive.Should().BeFalse();
            observed.Should().NotBeEmpty();
            directory.Count.Should().Be((Racers / 2) + 1);
        }
    }
}
