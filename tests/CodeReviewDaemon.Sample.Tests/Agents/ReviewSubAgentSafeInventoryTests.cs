using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// The safe inventory (<see cref="ReviewSubAgentTreeSnapshot.ToSafeInventory"/>) is the ONLY thing the
/// settled sub-agent roster contributes to the synthesis prompt. It is deliberately impoverished — name,
/// template, status and failure code, nothing else — because the synthesis turn reads the children's real
/// output through the delivered-result tools, not through a prompt-embedded transcript. Anything richer
/// here would smuggle agent/thread ids and raw failure text into the model's context, and would make the
/// prompt (and therefore the review) depend on transcript ordering.
/// </summary>
public sealed class ReviewSubAgentSafeInventoryTests
{
    private static ReviewSubAgentNode Node(
        string agentId,
        string template,
        ReviewSubAgentStatus status,
        string? name = null,
        string? failureCode = null
    ) =>
        new()
        {
            AgentId = agentId,
            ThreadId = $"thread-{agentId}",
            ParentThreadId = "thread-parent",
            Depth = 1,
            Status = status,
            Name = name,
            Template = template,
            FailureCode = failureCode,
        };

    [Fact]
    public void ToSafeInventory_reports_only_name_template_status_and_failure_code()
    {
        var snapshot = new ReviewSubAgentTreeSnapshot([
            Node("agent-1", "code-reviewer:security-review", ReviewSubAgentStatus.Completed, name: "security"),
            Node(
                "agent-2",
                "code-reviewer:performance-review",
                ReviewSubAgentStatus.Error,
                failureCode: "context_window"
            ),
        ]);

        var inventory = snapshot.ToSafeInventory();

        inventory.Should().Contain("security").And.Contain("code-reviewer:security-review").And.Contain("Completed");
        inventory
            .Should()
            .Contain("code-reviewer:performance-review")
            .And.Contain("Error")
            .And.Contain("context_window");
        inventory.Should().NotContain("agent-1").And.NotContain("agent-2");
        inventory.Should().NotContain("thread-agent-1").And.NotContain("thread-parent");
    }

    [Fact]
    public void ToSafeInventory_is_deterministic_regardless_of_snapshot_order()
    {
        var a = Node("agent-a", "code-reviewer:a", ReviewSubAgentStatus.Completed, name: "alpha");
        var b = Node("agent-b", "code-reviewer:b", ReviewSubAgentStatus.Stopped, name: "beta");

        new ReviewSubAgentTreeSnapshot([a, b])
            .ToSafeInventory()
            .Should()
            .Be(new ReviewSubAgentTreeSnapshot([b, a]).ToSafeInventory());
    }

    [Fact]
    public void ToSafeInventory_says_so_explicitly_when_no_sub_agents_ran()
    {
        // A blank inventory would read as a truncated prompt; the synthesis turn must be told plainly that
        // there is nothing delivered to fold in, so it synthesizes from its own analysis instead of hunting
        // for children that never existed.
        new ReviewSubAgentTreeSnapshot([])
            .ToSafeInventory()
            .Should()
            .Be(ReviewSubAgentTreeSnapshot.NoSubAgents);
    }
}
