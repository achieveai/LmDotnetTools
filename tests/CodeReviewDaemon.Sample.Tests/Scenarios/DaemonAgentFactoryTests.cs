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

    [Fact]
    public void ReviewProfile_Prompt_InstructsSkillSubAgentsAndInjectionSafety()
    {
        var prompt = DaemonAgentFactory.CreateReviewProfile().SystemPrompt;

        prompt.Should().Contain("code-reviewer"); // load the skill
        prompt.Should().Contain("Skill"); // via the Skill tool
        prompt.Should().Contain("Contracts/"); // cross-repo reading
        prompt.Should().MatchRegex("(?i)injection|untrusted"); // injection framing
        prompt.Should().MatchRegex("(?i)daemon.*post"); // daemon owns posting
    }

    [Fact]
    public void ReviewProfile_Prompt_GroundsViaReadByPathAndAvoidsRootGlob()
    {
        // The gateway's Glob/Grep cannot enumerate the repo root reliably, so the reviewer must ground via
        // Read of exact paths (using the injected manifest) and scope any search to a subdirectory rather
        // than globbing /workspace/target itself.
        var prompt = DaemonAgentFactory.CreateReviewProfile().SystemPrompt;

        prompt.Should().Contain("/workspace/target"); // the PR head checkout root
        prompt.Should().MatchRegex("(?i)exact path"); // Read files by exact path
        prompt.Should().MatchRegex("(?i)manifest"); // the manifest is provided in the input
        prompt.Should().MatchRegex("(?i)subdirector"); // scope Grep/Glob to a subdirectory
        prompt.Should().NotContain("Glob the workspace"); // the old root-glob instruction is gone
    }

    [Fact]
    public void ReviewProfile_Prompt_InstructsConsultingTheKnowledgeBase()
    {
        // Task 3 (design §3) — the reviewer consults the Knowledge Base carried in the checkout: Read the
        // _toc.md first, Grep/Read the entries relevant to the changed files, and call out when the PR
        // contradicts a recorded invariant.
        var prompt = DaemonAgentFactory.CreateReviewProfile().SystemPrompt;

        prompt.Should().Contain("KnowledgeBase"); // consult the KB in the checkout
        prompt.Should().Contain("_toc.md"); // start from the table of contents
        prompt.Should().MatchRegex("(?i)contradict"); // flag contradictions with known invariants
        prompt.Should().MatchRegex("(?i)invariant");
    }

    [Fact]
    public void CreateJudgeProfile_and_CreateKnowledgeProfile_have_stable_ids_and_gating()
    {
        // P4.4 — the executor feeds these to the live agent loop only when the judge / knowledge flags
        // are enabled. Each is a plain declaration: stable id, non-empty prompt, no built-ins, deferred
        // MCP allow-list.
        var judge = DaemonAgentFactory.CreateJudgeProfile();
        judge.Id.Should().Be(DaemonAgentFactory.JudgeProfileId);
        judge.SystemPrompt.Should().NotBeNullOrWhiteSpace();
        judge.EnabledBuiltInTools.Should().BeEmpty();
        judge.EnabledTools.Should().BeNull();

        var knowledge = DaemonAgentFactory.CreateKnowledgeProfile();
        knowledge.Id.Should().Be(DaemonAgentFactory.KnowledgeProfileId);
        knowledge.SystemPrompt.Should().NotBeNullOrWhiteSpace();
        knowledge.EnabledBuiltInTools.Should().BeEmpty();
        knowledge.EnabledTools.Should().BeNull();
    }
}
