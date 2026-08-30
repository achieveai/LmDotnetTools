using AchieveAi.LmDotnetTools.Misc.Utils;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
///     The #609 contract: the primary conversation hears a digest of EVERY board change, an assigned
///     agent hears changes inside its own subtree (dotted-path prefix, never assignee equality),
///     bursts coalesce into one message per window, hydration is silent, and a net-zero window
///     delivers nothing.
/// </summary>
public class TodoDigestServiceTests
{
    private sealed class Harness
    {
        public FakeTimeProvider Clock { get; } = new();

        public TaskManager Manager { get; }

        /// <summary>Target null is the primary (root) conversation, mirroring the Program.cs wiring.</summary>
        public List<(string? Target, NotifyMessage Message)> Delivered { get; } = [];

        public Func<string, TodoNudgeTargetKind> Resolver { get; set; } = _ => TodoNudgeTargetKind.SubAgent;

        public TodoDigestService Service { get; private set; } = null!;

        public Harness()
        {
            Manager = new TaskManager(Clock);
        }

        /// <summary>
        ///     Constructs the service over the manager's CURRENT board and subscribes it to OnChanged,
        ///     exactly like Program.cs — everything seeded before this call is hydrated baseline.
        /// </summary>
        public TodoDigestService Build(TodoDigestOptions? options = null)
        {
            Service = new TodoDigestService(
                options ?? new TodoDigestOptions(),
                Manager.GetTasks,
                name => Resolver(name),
                (name, message, _) =>
                {
                    Delivered.Add((name, message));
                    return ValueTask.FromResult(true);
                },
                Clock
            );
            Manager.OnChanged += Service.OnBoardChangedHook;
            return Service;
        }

        /// <summary>Fires the pending flush (if a window is open) by crossing the debounce window.</summary>
        public void AdvancePastWindow()
        {
            Clock.Advance(TodoDigestService.DebounceWindow);
        }

        public List<string?> Targets => [.. Delivered.Select(d => d.Target)];

        public string DetailFor(string? target)
        {
            return Delivered.Single(d => d.Target == target).Message.Detail!;
        }
    }

    private static TodoDigestOptions PrimaryOnly => new() { AssigneeDigestEnabled = false };

    // ---------------------------------------------------------------- debounce & coalescing

    [Fact]
    public void Burst_CoalescesIntoOneMessagePerWindow()
    {
        var harness = new Harness();
        _ = harness.Build(PrimaryOnly);

        _ = harness.Manager.AddTask("Design");
        _ = harness.Manager.AddTask("Build");
        _ = harness.Manager.AssignTask("1", "rev-a");
        harness.Delivered.Should().BeEmpty("nothing flushes before the window closes");

        harness.AdvancePastWindow();

        var (target, message) = harness.Delivered.Should().ContainSingle().Subject;
        target.Should().BeNull("the primary digest addresses the root conversation");
        message.NotifyKind.Should().Be(NotifyKinds.TodoDigest);
        message.Detail.Should().Contain("1 added: Design").And.Contain("assignee rev-a").And.Contain("2 added: Build");
    }

    [Fact]
    public void Window_IsFixed_NotSliding()
    {
        // Pins the fixed window: a change landing INSIDE an open window must not re-arm the timer.
        // A sliding re-arm postpones the flush past the assertion below and goes red.
        var harness = new Harness();
        _ = harness.Build(PrimaryOnly);

        _ = harness.Manager.AddTask("First");
        harness.Clock.Advance(TodoDigestService.DebounceWindow - TimeSpan.FromSeconds(1));
        harness.Delivered.Should().BeEmpty("the window has not closed yet");

        _ = harness.Manager.AddTask("Second, inside the open window");
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        var (_, message) = harness.Delivered.Should().ContainSingle().Subject;
        message.Detail.Should().Contain("1 added").And.Contain("2 added", "both changes ride the one flush");
    }

    [Fact]
    public void SecondBurst_ProducesASecondDigest()
    {
        // The other half of the debounce contract: the window must re-open after a flush — a service
        // that never re-arms digests exactly once and falls silent.
        var harness = new Harness();
        _ = harness.Build(PrimaryOnly);
        _ = harness.Manager.AddTask("First window");
        harness.AdvancePastWindow();
        harness.Delivered.Should().HaveCount(1);

        _ = harness.Manager.AddTask("Second window");
        harness.AdvancePastWindow();

        harness.Delivered.Should().HaveCount(2);
        harness.Delivered[1].Message.Detail.Should().Contain("2 added").And.NotContain("1 added");
    }

    // ---------------------------------------------------------------- transition lines

    [Fact]
    public void Digest_DescribesClaimCompleteAndBlockTransitions()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Claim me");
        _ = harness.Manager.AddTask("Complete me");
        _ = harness.Manager.AddTask("Block me");
        _ = harness.Build(PrimaryOnly);

        _ = harness.Manager.ClaimTask("1", "rev-a");
        _ = harness.Manager.ClaimTask("2", "rev-b");
        _ = harness.Manager.UpdateTask("2", "completed");
        _ = harness.Manager.BlockTask("3", ["1"]);
        harness.AdvancePastWindow();

        var (_, message) = harness.Delivered.Should().ContainSingle().Subject;
        message.Detail.Should().StartWith("Todo board changes: ");
        // Task 2 was claimed AND completed inside one window: the digest reports the NET transition.
        message.Detail.Should().Contain("1 claimed by rev-a").And.Contain("2 completed").And.Contain("3 blocked by 1");
        message.Detail.Should().NotContain("2 claimed", "intermediate states inside one window collapse");
    }

    [Fact]
    public void NetZeroWindow_DeliversNothing_ButTheServiceStaysLive()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Reassigned back and forth");
        _ = harness.Manager.AssignTask("1", "rev-a");
        _ = harness.Build(PrimaryOnly);

        // Two changes that net out inside one window: the flush finds every row fingerprint-equal.
        _ = harness.Manager.AssignTask("1", "rev-b");
        _ = harness.Manager.AssignTask("1", "rev-a");
        harness.AdvancePastWindow();
        harness.Delivered.Should().BeEmpty("a net-zero flush has nothing to say");

        // Non-vacuity: the same service still digests a REAL change afterwards, so the silence
        // above was a decision, not a dead timer.
        _ = harness.Manager.ClaimTask("1", "rev-a");
        harness.AdvancePastWindow();
        harness.Delivered.Should().ContainSingle().Which.Message.Detail.Should().Contain("1 claimed by rev-a");
    }

    [Fact]
    public void ClaimRefreshHeartbeat_IsNetZero()
    {
        // A claim refresh touches only the lease timestamp; the row fingerprint is timestamp-free
        // on purpose, so the heartbeat opens a window whose flush finds nothing to say.
        var harness = new Harness();
        _ = harness.Manager.AddTask("Leased work");
        _ = harness.Manager.ClaimTask("1", "rev-a");
        _ = harness.Build(PrimaryOnly);

        _ = harness.Manager.ClaimTask("1", "rev-a");
        harness.AdvancePastWindow();

        harness.Delivered.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- hydration

    [Fact]
    public void Hydration_IsSilent_AndTheBaselineIsRemembered()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Pre-existing");
        _ = harness.Manager.AddTask("Also pre-existing");
        _ = harness.Manager.AssignTask("1", "agent-a");
        _ = harness.Build(PrimaryOnly);

        harness.AdvancePastWindow();
        harness.Delivered.Should().BeEmpty("construction over a hydrated board digests nothing");

        _ = harness.Manager.ClaimTask("1", "agent-a");
        harness.AdvancePastWindow();

        // Baseline suppression must come from REMEMBERING the hydrated rows, not forgetting them:
        // a service that started empty would list the pre-existing rows as "added" here.
        var (_, message) = harness.Delivered.Should().ContainSingle().Subject;
        message.Detail.Should().Contain("1 claimed by agent-a").And.NotContain("added");
    }

    // ---------------------------------------------------------------- subtree scoping

    [Fact]
    public void Subtree_GrandchildIn_SiblingOut()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("Parent"); // 1
        _ = harness.Manager.AddTask("Child", "1"); // 1.1
        _ = harness.Manager.AddTask("Grandchild", "1.1"); // 1.1.1
        _ = harness.Manager.AddTask("Elsewhere"); // 2
        _ = harness.Manager.AssignTask("1", "agent-a");
        _ = harness.Manager.AssignTask("2", "agent-b");
        _ = harness.Build();

        _ = harness.Manager.ClaimTask("1.1.1", "agent-a");
        harness.AdvancePastWindow();

        harness.Targets.Should().BeEquivalentTo([null, "agent-a"]);
        harness
            .DetailFor("agent-a")
            .Should()
            .Contain("1.1.1 claimed by agent-a", "a grandchild is inside agent-a's subtree");
    }

    [Fact]
    public void Subtree_Task11_IsNotUnderTask1()
    {
        // Mutation pin for the prefix guard: membership is `id == root || id starts with root + "."`.
        // Drop the appended dot and "11".StartsWith("1") makes agent-a hear task 11's change — red.
        var harness = new Harness();
        for (var i = 1; i <= 11; i++)
        {
            _ = harness.Manager.AddTask($"Task {i}");
        }

        _ = harness.Manager.AssignTask("1", "agent-a");
        _ = harness.Build();

        _ = harness.Manager.UpdateTask("11", "removed");
        harness.AdvancePastWindow();
        harness
            .Targets.Should()
            .BeEquivalentTo([null], "task 11 is not inside task 1's subtree, so only the primary digest fires");
        harness.Delivered.Clear();

        // Positive control: the same agent DOES hear a change to its own task, so the assertion
        // above cannot pass because assignee digests are off entirely.
        _ = harness.Manager.UpdateTask("1", "removed");
        harness.AdvancePastWindow();
        harness.Targets.Should().BeEquivalentTo([null, "agent-a"]);
    }

    [Fact]
    public void Subtree_ChildOverriddenToAnotherAgent_NotifiesBothAssignees()
    {
        // Membership is computed from the assigned task's subtree at diff time, never by assignee
        // equality: 1.1 belongs to agent-b, but it also lies below agent-a's task 1.
        var harness = new Harness();
        _ = harness.Manager.AddTask("Parent"); // 1
        _ = harness.Manager.AddTask("Child", "1"); // 1.1
        _ = harness.Manager.AssignTask("1", "agent-a");
        _ = harness.Manager.AssignTask("1.1", "agent-b");
        _ = harness.Build();

        _ = harness.Manager.ClaimTask("1.1", "agent-b");
        harness.AdvancePastWindow();

        harness.Targets.Should().BeEquivalentTo([null, "agent-a", "agent-b"]);
    }

    [Fact]
    public void AssigneeDigest_ScopesEachAgentToItsOwnSubtree()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("For a"); // 1
        _ = harness.Manager.AddTask("For b"); // 2
        _ = harness.Manager.AssignTask("1", "agent-a");
        _ = harness.Manager.AssignTask("2", "agent-b");
        _ = harness.Build();

        _ = harness.Manager.ClaimTask("1", "agent-a");
        _ = harness.Manager.ClaimTask("2", "agent-b");
        harness.AdvancePastWindow();

        harness.Targets.Should().BeEquivalentTo([null, "agent-a", "agent-b"]);
        harness.DetailFor(null).Should().Contain("1 claimed by agent-a").And.Contain("2 claimed by agent-b");
        harness.DetailFor("agent-a").Should().Contain("1 claimed").And.NotContain("2 claimed");
        harness.DetailFor("agent-b").Should().Contain("2 claimed").And.NotContain("1 claimed");
    }

    // ---------------------------------------------------------------- audiences & gating

    [Fact]
    public void Root_AlwaysHears_EvenWhenNoNameResolvesToASubAgent()
    {
        // The nudge-side NudgeRootConversation opt-in does NOT apply to digests: the primary digest
        // goes to the root unconditionally, and an assignee whose conversation IS the root is
        // skipped rather than duplicated.
        var harness = new Harness { Resolver = _ => TodoNudgeTargetKind.RootConversation };
        _ = harness.Manager.AddTask("Root-owned work");
        _ = harness.Manager.AssignTask("1", "root-persona");
        _ = harness.Build();

        _ = harness.Manager.ClaimTask("1", "root-persona");
        harness.AdvancePastWindow();

        harness.Targets.Should().BeEquivalentTo([null]);
    }

    [Fact]
    public void PrimaryDisabled_AssigneeDigestsStillFlow()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("For a");
        _ = harness.Manager.AssignTask("1", "agent-a");
        _ = harness.Build(new TodoDigestOptions { PrimaryDigestEnabled = false });

        _ = harness.Manager.ClaimTask("1", "agent-a");
        harness.AdvancePastWindow();

        harness.Targets.Should().BeEquivalentTo(["agent-a"]);
    }

    [Fact]
    public void AssigneeDisabled_OnlyThePrimaryDigestFlows()
    {
        var harness = new Harness();
        _ = harness.Manager.AddTask("For a");
        _ = harness.Manager.AssignTask("1", "agent-a");
        _ = harness.Build(new TodoDigestOptions { AssigneeDigestEnabled = false });

        _ = harness.Manager.ClaimTask("1", "agent-a");
        harness.AdvancePastWindow();

        harness.Targets.Should().BeEquivalentTo([null]);
    }
}
