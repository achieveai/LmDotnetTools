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

    [Fact]
    public void CreateVariantProfile_carries_the_variant_prompt_and_keeps_the_same_tool_gating()
    {
        // P4.2 — the prompt/skill axis of an A/B comparison feeds the profile; the model and the
        // write capability are applied by the executor, not baked into the declarative profile.
        var variant = new ReviewVariant(
            VariantId: "b",
            ModelId: "anthropic/claude-haiku-4-5",
            SystemPrompt: "Review tersely; flag only blocking issues.",
            CanWrite: false);

        var profile = DaemonAgentFactory.CreateVariantProfile(variant);

        profile.Id.Should().Be($"{DaemonAgentFactory.ReviewProfileId}-b");
        profile.SystemPrompt.Should().Be("Review tersely; flag only blocking issues.");
        profile.EnabledBuiltInTools.Should().BeEmpty();
        profile.EnabledTools.Should().BeNull();
    }

    [Fact]
    public void CreateVariantProfile_rejects_a_blank_prompt()
    {
        var variant = new ReviewVariant("b", "model", SystemPrompt: "   ", CanWrite: false);

        var act = () => DaemonAgentFactory.CreateVariantProfile(variant);

        act.Should().Throw<ArgumentException>();
    }
}
