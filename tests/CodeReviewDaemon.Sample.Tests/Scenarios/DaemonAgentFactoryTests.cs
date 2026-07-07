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
    public void CreateReviewProfile_with_variables_renders_the_concrete_workspace_layout()
    {
        // The daemon YAML/Scriban prompt template (Prompts/daemon-prompts.yaml) templates the run's
        // concrete checkout/store/notes paths into the review agent's system prompt, so it is TOLD exactly
        // where to read and where to write instead of guessing.
        var vars = new Dictionary<string, object>
        {
            ["checkout_root"] = "/workspace/store/repos/Foo",
            ["has_store"] = true,
            ["store_root"] = "/workspace/store",
            ["has_notes"] = true,
            ["notes_dir"] = "/workspace/store/PRs/github/acme/1",
        };

        var prompt = DaemonAgentFactory.CreateReviewProfile(vars).SystemPrompt;

        prompt.Should().Contain("/workspace/store/repos/Foo");
        prompt.Should().Contain("cross-repo store at /workspace/store");
        prompt.Should().Contain("/workspace/store/PRs/github/acme/1");
        prompt.Should().MatchRegex("(?i)only writable location");
    }

    [Fact]
    public void CreateReviewProfile_with_variables_omits_store_and_notes_sentences_when_absent()
    {
        var vars = new Dictionary<string, object>
        {
            ["checkout_root"] = "/workspace/target",
            ["has_store"] = false,
            ["store_root"] = string.Empty,
            ["has_notes"] = false,
            ["notes_dir"] = string.Empty,
        };

        var prompt = DaemonAgentFactory.CreateReviewProfile(vars).SystemPrompt;

        prompt.Should().Contain("/workspace/target"); // the checkout root still renders
        prompt.Should().NotContain("cross-repo store at"); // the has_store sentence is omitted
        prompt.Should().NotMatchRegex("(?i)only writable location"); // the has_notes sentence is omitted
        prompt.Should().NotMatchRegex(@"\{\{|\}\}"); // no leftover Scriban syntax
    }

    [Fact]
    public void CreateVariantProfile_with_variables_renders_the_variant_prompt_through_scriban()
    {
        // The A/B comparison arm's prompt can carry the same {{ }} placeholders as the primary review
        // template; the executor renders it with the same variables dictionary.
        var variant = new ReviewVariant(
            VariantId: "b",
            ModelId: "anthropic/claude-haiku-4-5",
            SystemPrompt: "Review tersely. Workspace: {{ checkout_root }}.",
            CanWrite: false);
        var vars = new Dictionary<string, object> { ["checkout_root"] = "/workspace/target" };

        var profile = DaemonAgentFactory.CreateVariantProfile(variant, vars);

        profile.SystemPrompt.Should().Be("Review tersely. Workspace: /workspace/target.");
    }

    [Fact]
    public void CreateJudgeProfile_has_a_stable_id_and_gating()
    {
        // P4.4 — the executor feeds this to the live agent loop only when the judge flag is enabled. It is
        // a plain declaration: stable id, non-empty prompt, no built-ins, deferred MCP allow-list.
        var judge = DaemonAgentFactory.CreateJudgeProfile();
        judge.Id.Should().Be(DaemonAgentFactory.JudgeProfileId);
        judge.SystemPrompt.Should().NotBeNullOrWhiteSpace();
        judge.EnabledBuiltInTools.Should().BeEmpty();
        judge.EnabledTools.Should().BeNull();
    }

    [Fact]
    public void CreateKnowledgeExtractionProfile_carries_the_gate_and_marker_contract()
    {
        // Task 4 (design §1/§2) — the at-close extraction profile: gate sentinel + the header markers the
        // daemon parses, and an explicit "do not write frontmatter" instruction (the daemon injects it).
        var profile = DaemonAgentFactory.CreateKnowledgeExtractionProfile();

        profile.Id.Should().Be(DaemonAgentFactory.KnowledgeExtractionProfileId);
        profile.EnabledBuiltInTools.Should().BeEmpty();
        profile.EnabledTools.Should().BeNull();

        var prompt = profile.SystemPrompt;
        prompt.Should().Contain("NO_KNOWLEDGE"); // the gate sentinel
        prompt.Should().Contain("## SCOPE:");
        prompt.Should().Contain("## TITLE:");
        prompt.Should().Contain("## TAGS:");
        prompt.Should().Contain("## UPDATES:");
        prompt.Should().MatchRegex("(?i)frontmatter"); // the model must NOT write frontmatter
        prompt.Should().MatchRegex("(?i)durable");
    }
}
