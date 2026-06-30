using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.0 — the review <see cref="AchieveAi.LmDotnetTools.LmAgentInfra.AgentProfile"/> is built
/// declaratively: a stable id, a non-empty system prompt, and tool gating that grants the reviewer no
/// provider built-in tools while leaving the MCP allow-list to the capability-enforcing executor.
/// </summary>
public sealed class DaemonAgentFactoryTests
{
    [Fact]
    public void CreateReviewProfile_has_stable_identity_and_a_system_prompt()
    {
        var profile = DaemonAgentFactory.CreateReviewProfile();

        profile.Id.Should().Be(DaemonAgentFactory.ReviewProfileId);
        profile.Name.Should().Be("Review Agent");
        profile.SystemPrompt.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CreateReviewProfile_grants_no_built_in_tools_and_defers_the_mcp_allow_list()
    {
        var profile = DaemonAgentFactory.CreateReviewProfile();

        // Empty built-in list = the reviewer gets no provider built-ins (e.g. web_search).
        profile.EnabledBuiltInTools.Should().NotBeNull();
        profile.EnabledBuiltInTools.Should().BeEmpty();

        // null MCP allow-list = gating is applied later by the capability-enforcing executor (P4.2),
        // not baked into the profile.
        profile.EnabledTools.Should().BeNull();
    }

    [Fact]
    public void CreateReviewProfile_is_deterministic()
    {
        var first = DaemonAgentFactory.CreateReviewProfile();
        var second = DaemonAgentFactory.CreateReviewProfile();

        first.Id.Should().Be(second.Id);
        first.Name.Should().Be(second.Name);
        first.SystemPrompt.Should().Be(second.SystemPrompt);
        first.EnabledTools.Should().BeEquivalentTo(second.EnabledTools);
        first.EnabledBuiltInTools.Should().BeEquivalentTo(second.EnabledBuiltInTools);
    }
}
