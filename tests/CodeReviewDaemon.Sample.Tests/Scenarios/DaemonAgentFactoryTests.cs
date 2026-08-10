using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.0 — the review <see cref="AchieveAi.LmDotnetTools.LmAgentInfra.AgentProfile"/> is built
/// declaratively: a stable id, a non-empty system prompt, and tool gating that grants the reviewer no
/// provider built-in tools while leaving the MCP allow-list to the capability-enforcing executor.
/// </summary>
public sealed class DaemonAgentFactoryTests
{
    /// <summary>Renders the AUTHORITATIVE synthesis prompt — the one and only turn that delivers — with the
    /// caller's real posting intent. The provider-specific posting contract lives here, never on the
    /// collect-only first turn.</summary>
    private static string SynthesisPrompt(bool shouldPost, bool isAdo = false, string botName = "Revobot") =>
        DaemonAgentFactory.CreateSynthesisPrompt(
            new Dictionary<string, object>
            {
                ["bot_name"] = botName,
                ["should_post"] = shouldPost,
                ["is_ado"] = isAdo,
                ["gh_owner"] = "acme",
                ["gh_repo"] = "widgets",
                ["ado_org"] = "acme-org",
                ["ado_project"] = "acme-project",
                ["ado_repo"] = "widgets",
                ["pr_number"] = "118",
            },
            "- code-reviewer:architecture-review (architecture) — completed");

    [Fact]
    public void CreateReviewProfile_with_variables_renders_the_bot_name_into_the_identity_and_self_reference()
    {
        // The daemon prepends "[BotName]" to the POSTED comment; injecting bot_name here lets the review
        // BODY self-identify with the SAME name instead of a label the model invents ad-hoc.
        var vars = new Dictionary<string, object>
        {
            ["bot_name"] = "Revobot",
            ["checkout_root"] = "/workspace/target",
        };

        var prompt = DaemonAgentFactory.CreateReviewProfile(vars).SystemPrompt;

        prompt.Should().Contain("You are Revobot,"); // identity line
        prompt.Should().Contain("use the name Revobot"); // self-reference directive
        prompt.Should().NotContain("You are ,"); // never render an empty name
    }

    [Fact]
    public void CreateReviewProfile_defaults_the_bot_name_when_no_variables_are_supplied()
    {
        // The variable-less overload (used by the declarative-profile tests) must still render a clean
        // identity line rather than "You are , an …".
        var prompt = DaemonAgentFactory.CreateReviewProfile().SystemPrompt;

        prompt.Should().Contain("You are Revobot,");
        prompt.Should().NotContain("You are ,");
    }

    [Fact]
    public void CreateReviewProfile_has_stable_identity_and_a_system_prompt()
    {
        var profile = DaemonAgentFactory.CreateReviewProfile();

        profile.Id.Should().Be(DaemonAgentFactory.ReviewProfileId);
        profile.Name.Should().Be("Review Agent");
        profile.SystemPrompt.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ReviewProfile_Prompt_EncodesTheFourReviewRequirements()
    {
        // The user's standing rules for how Revobot reviews (see memory revobot-review-posting-requirements):
        // (1) review the FULL PR; (2/5) judge resolution from each thread's conversation, not just a status
        // hint; (3) weigh comments from ALL authors (bots + humans); (4) answer a question directed at the
        // bot. These shape the FINDINGS, so they are unconditional on the collect-only first turn — the
        // caller's should_post never gates them. Rule (6), the past-vs-new split, is NOT among them: it is
        // meaningful only once there is a last review to be new since, so it moved under is_rereview in
        // prompt v1.2 and is pinned by the test below.
        var prompt = DaemonAgentFactory.CreateReviewProfile(
            new Dictionary<string, object> { ["bot_name"] = "Revobot", ["should_post"] = true }).SystemPrompt;

        prompt.Should().Contain("FULL PR"); // (1) review the whole PR, not just a sample/delta
        prompt.Should().MatchRegex("(?i)ALL AUTHORS"); // (3) consider other bots + humans
        prompt.Should().MatchRegex("(?i)resolved"); // (2/5) resolution judgment, not blind re-post
        prompt.Should().Contain("[status]"); // (5) status is a HINT — the LLM decides resolution
        prompt.Should().MatchRegex("(?i)ANSWER any question"); // (4) a question aimed at the bot must be answered
    }

    [Fact]
    public void ReviewProfile_Prompt_withholds_the_no_op_exit_until_there_is_a_prior_review()
    {
        // The trap this pins: v1.0 and v1.1 opened the existing-comments block with "On a re-review, …" and
        // then stated every rule in it UNCONDITIONALLY — including "make your final review exactly 'No new
        // findings since the last review.'" A first-time reviewer has, by construction, no last review to
        // have found anything since, so the cheapest way to satisfy that sentence is to emit it and never
        // open the diff. That is what 51 of 104 PRs in the NOVA fleet came back as. Fixing the daemon-side
        // dedup brief (FirstReviewExistingCommentsGuidance) was necessary but not sufficient: runs 131/132
        // were first runs, ran against the fixed brief, and still took the exit — the prompt offered it on
        // its own. So the exit and its delta framing must be reachable ONLY when is_rereview is true.
        //
        // THIS TEST WAS VACUOUS FOR ITS WHOLE LIFE BEFORE THE APPENDIX HAD A READER. It asserts a property
        // of profile.SystemPrompt, and profile.SystemPrompt reached the hosted agent through exactly one
        // channel — ProvisionAsync's systemPromptAppendix — which the host stored and never read. So no
        // model ever saw the prompt this constrains, which is why the sentinel kept appearing on first
        // reviews no matter what the prompt said, and why only the code-side guard fixed it. It is
        // load-bearing again only because every link is now pinned:
        //   1. this test                                      profile prompt withholds/keeps the exit
        //   2. S2SReviewAgentTests:118                        profile prompt -> provision systemPromptAppendix
        //   3. ConversationsControllerTests
        //      Provision_PersistsTheCallerInstructions_...    provision -> thread property -> composed prompt
        // Break any one and this assertion goes quiet again without going red. If you delete a link, delete
        // this test with it rather than leaving it green.
        const string NoOpExit = "No new findings since the last review.";
        const string Split = "New comments since your last review";

        var firstReview = DaemonAgentFactory.CreateReviewProfile(
            new Dictionary<string, object> { ["bot_name"] = "Revobot" }).SystemPrompt;

        firstReview.Should().NotContain(NoOpExit, "a first-time reviewer must have no way to opt out of reviewing");
        firstReview.Should().NotContain(Split, "there is no earlier round for comments to be new since");

        var reReview = DaemonAgentFactory.CreateReviewProfile(
            new Dictionary<string, object>
            {
                ["bot_name"] = "Revobot",
                ["is_rereview"] = true,
                ["review_round"] = "02",
                ["prev_commit"] = "abc123",
                ["new_commit"] = "def456",
            }).SystemPrompt;

        // Withheld, not deleted: on a genuine re-review "nothing new" is a legitimate, useful outcome.
        reReview.Should().Contain(NoOpExit);
        reReview.Should().Contain(Split);
    }

    [Fact]
    public void ReviewProfile_Prompt_no_longer_asks_the_reviewer_to_gather_work_item_context_itself()
    {
        // This REPLACES two tests that pinned the opposite: that the prompt named
        // code-reviewer:pr-context-gatherer and made the reviewer report which of three outcomes befell its
        // lookup. Both are obsolete, and not because the goal changed — because the mechanism did. The
        // capability was offered to the model in the prompt and the environment could not execute it: across
        // 644 observed review sub-agent spawns ZERO carried a tool that could reach ADO, and the one dispatch
        // in 698 spawns had nothing to do the job with. The daemon now performs the lookup itself
        // (AdoWorkItemContextReader) and hands the reviewer the answer.
        //
        // So the instruction has to GO, rather than sit there unexecutable: a prompt that tells the model to
        // dispatch a gatherer makes it narrate a step the daemon already performed, which is the same defect
        // family as reviews narrating our infrastructure to PR authors.
        var prompt = DaemonAgentFactory.CreateReviewProfile(
            new Dictionary<string, object> { ["bot_name"] = "Revobot" }).SystemPrompt;

        prompt.Should().NotContain(
            "pr-context-gatherer",
            "the daemon fetches the work items in code; asking the reviewer to dispatch an agent for it "
                + "makes it narrate work that already happened");

        // The negative above is only worth having beside the positive: the reviewer must still be TOLD the
        // context exists, or the block lands in a brief nobody was pointed at. A bare NotContain would stay
        // green if the block were never mentioned at all, which is the failure this pairs against.
        prompt.Should().Contain(
            "## Work items linked to this pull request",
            "the reviewer has to be told the block exists and what it is");
        prompt.Should().MatchRegex(
            "(?i)lookup FAILED",
            "the three outcomes are now distinguished IN the block, so the reviewer must be told to read "
                + "them apart rather than to report on its own attempt");
    }

    [Fact]
    public void ReviewProfile_Prompt_does_not_let_the_no_network_clause_excuse_the_work_item_lookup()
    {
        // Step 5 states as flat fact that the sandbox "has no toolchain and no network". That clause is
        // correct about WHY CI is the only build evidence — it must not generalise into a blanket claim that
        // nothing outside the sandbox is reachable, because the daemon reaches plenty on the reviewer's
        // behalf and hands the results over in the brief.
        var prompt = DaemonAgentFactory.CreateReviewProfile(
            new Dictionary<string, object> { ["bot_name"] = "Revobot" }).SystemPrompt;

        var index = prompt.IndexOf("no toolchain and no network", StringComparison.Ordinal);
        index.Should().BeGreaterThan(-1);
        prompt[index..].Should().MatchRegex(
            "(?i)not a general statement",
            "the build-evidence clause must be scoped, or it reads as a blanket unreachability claim");
        prompt[index..].Should().MatchRegex(
            "(?i)no lookup for\\s+you to attempt",
            "and the scoping must say what replaced the lookup, or a reviewer told the network is dead has "
                + "no reason to trust a block that could only have come over it");
    }

    [Fact]
    public void ReviewProfile_Prompt_requires_a_compile_or_runtime_verdict_to_cite_what_it_was_inferred_from()
    {
        // Measured across 169 reviews of record: zero claims of having built or tested anything, and ten
        // assertions about compile/test/runtime OUTCOMES — every one of them a static inference that showed
        // its evidence (run 174 quoted the removed import AND the surviving usage before saying the Scala
        // source no longer compiles). This pins that behaviour rather than repairing it: the reviewer already
        // does this ten times out of ten, and the clause exists so a model or prompt change cannot quietly
        // drop it. It also inlines the two rules from code-reviewer:codebase-search-discipline that matter
        // most here, because that skill's reference document fails to load in roughly half of all reviews.
        var prompt = DaemonAgentFactory.CreateReviewProfile(
            new Dictionary<string, object> { ["bot_name"] = "Revobot" }).SystemPrompt;

        prompt.Should().MatchRegex(
            "(?i)inference stated as an observation",
            "a sound inference is welcome; one dressed as something observed is not");
        prompt.Should().MatchRegex(
            "(?i)unable to find",
            "search-discipline rule 6 — qualify uncertainty rather than asserting absence");
        prompt.Should().MatchRegex(
            "(?i)green build",
            "search-discipline rule 4 — a passing build outranks a search that missed the symbol");
    }

    [Fact]
    public void ReviewProfile_Prompt_says_where_a_required_answer_goes_on_the_collect_turn()
    {
        // Observed on NOVA run 138 (PR 5503135, round 02): the prompt requires answering a question aimed at
        // the bot, but the collect turn has no delivery channel — this profile is rendered with should_post
        // forced false, and the DELIVERY section forbids any provider write. The agent resolved that
        // contradiction the only way left to it: it curl'd the Azure DevOps threads API, the sandbox's egress
        // policy refused the write, and the round's final review opened "Unable to post the required in-thread
        // answer…". It spent the round on a blocked write instead of on reviewing.
        // The duty is not the bug — the missing channel is. So the instruction must NAME where the answer
        // goes: into the review text, which is the thing this turn actually produces and the daemon delivers.
        var prompt = DaemonAgentFactory.CreateReviewProfile(
            new Dictionary<string, object> { ["bot_name"] = "Revobot", ["should_post"] = true }).SystemPrompt;

        var answerIndex = prompt.IndexOf("ANSWER any question", StringComparison.Ordinal);
        answerIndex.Should().BeGreaterThan(-1);
        // Read the sentence that carries the duty, not the whole prompt: a rule stated here and contradicted
        // three paragraphs later is exactly what run 138 was handed.
        var sentence = prompt[answerIndex..Math.Min(prompt.Length, answerIndex + 400)];
        sentence.Should().MatchRegex(
            "(?i)(in|into) the review",
            "the answer has to go somewhere this turn can actually put it — the review text the daemon delivers");
        sentence.Should().MatchRegex(
            "(?i)do not (post|reply|deliver)|never post",
            "and the instruction must say, right there, that answering is not posting");
    }

    [Fact]
    public void ReviewProfile_Prompt_keeps_the_sub_agent_duty_alive_on_a_re_review()
    {
        // The other half of run 138: round 01 dispatched 4 sub-agents, round 02 dispatched 0 — the daemon's
        // completion barrier settled with zero children and the run produced 0 reviewer transcripts. The
        // dispatch duty is stated once, in step 2, in first-review terms; the re-review block then restates
        // only the obligations round 02 went on to satisfy (answer the new comments, "nothing new" is a valid
        // outcome). What is restated at the point of use is what gets followed, so the delta arrived
        // unreviewed by any dimension agent while the round still reported itself complete.
        var reReview = DaemonAgentFactory.CreateReviewProfile(
            new Dictionary<string, object>
            {
                ["bot_name"] = "Revobot",
                ["is_rereview"] = true,
                ["review_round"] = "02",
                ["prev_commit"] = "abc123",
                ["new_commit"] = "def456",
            }).SystemPrompt;

        var exitIndex = reReview.IndexOf("No new findings since the last review.", StringComparison.Ordinal);
        exitIndex.Should().BeGreaterThan(-1);
        var reReviewBlock = reReview[Math.Max(0, exitIndex - 1200)..];
        reReviewBlock.Should().MatchRegex(
            "(?i)sub-agent",
            "the re-review block must restate the fan-out duty where the round is actually framed");
        reReviewBlock.Should().MatchRegex(
            "(?i)answering .{0,60}(is not|does not)",
            "answering a question is not a substitute for re-reviewing the delta — round 02 treated it as one");
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
    public void CreateReviewProfile_with_variables_renders_the_rereview_section()
    {
        // A re-review is told it's a re-review, sees the previously-reviewed commit and the current
        // head, and is pointed at its own prior notes files (round 02, since one prior round already
        // completed). It is NOT told to write this round's numbered files — the daemon authors those.
        var vars = new Dictionary<string, object>
        {
            ["checkout_root"] = "/workspace/target",
            ["has_store"] = false,
            ["store_root"] = string.Empty,
            ["has_notes"] = true,
            ["notes_dir"] = "/workspace/store/PRs/acme-1",
            ["is_rereview"] = true,
            ["prev_commit"] = "abc123",
            ["new_commit"] = "def456",
            ["review_round"] = "02",
            ["has_prior_files"] = true,
            ["prior_files"] = "PR_Context_01.md\nPR_Findings_01.md",
        };

        var prompt = DaemonAgentFactory.CreateReviewProfile(vars).SystemPrompt;

        prompt.Should().MatchRegex("(?i)RE-REVIEW");
        prompt.Should().Contain("round 02");
        prompt.Should().Contain("abc123");
        prompt.Should().Contain("def456");
        prompt.Should().Contain("PR_Findings_01.md");
        // The prompt no longer dictates a write convention: ReviewNotesArtifactBuilder in the daemon
        // authors PR_Context_NN.md / PR_Findings_NN_*.md deterministically after the round completes,
        // so the agent is never asked (and never told how) to write them itself.
        prompt.Should().NotContain("PR_Context_02.md");
        prompt.Should().NotContain("PR_Findings_02.md");
    }

    [Fact]
    public void CreateReviewProfile_with_variables_omits_rereview_section_on_the_first_review()
    {
        var vars = new Dictionary<string, object>
        {
            ["checkout_root"] = "/workspace/target",
            ["has_store"] = false,
            ["store_root"] = string.Empty,
            ["has_notes"] = true,
            ["notes_dir"] = "/workspace/store/PRs/acme-1",
            ["is_rereview"] = false,
            ["prev_commit"] = string.Empty,
            ["new_commit"] = "def456",
            ["review_round"] = "01",
            ["has_prior_files"] = false,
            ["prior_files"] = string.Empty,
        };

        var prompt = DaemonAgentFactory.CreateReviewProfile(vars).SystemPrompt;

        // The re-review SECTION (head/round/prior-files) is what must be absent. The generic sentence
        // explaining the "## Already posted on this PR" input section is unconditional — that section is
        // prepended to the review input whatever the round, and reading it is collect-only work.
        prompt.Should().NotContain("This is a RE-REVIEW"); // no re-review section on a first review
        prompt.Should().NotContain("abc123");
        // No write convention in any round — the daemon owns the numbered artifacts. What the agent
        // IS still told is where its scratch notes may go.
        prompt.Should().NotContain("PR_Context_01.md");
        prompt.Should().NotContain("PR_Findings_01.md");
        prompt.Should().Contain("/workspace/store/PRs/acme-1");
        prompt.Should().NotMatchRegex(@"\{\{|\}\}"); // no leftover Scriban syntax
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
        // A GitHub run (is_ado unset) reviews via the code-reviewer:pr-review skill and its sub-agents, and
        // the first turn is explicitly COLLECT-ONLY even though the caller asked to post.
        var prompt = DaemonAgentFactory.CreateReviewProfile(
            new Dictionary<string, object> { ["bot_name"] = "Revobot", ["should_post"] = true }).SystemPrompt;

        prompt.Should().Contain("code-reviewer"); // load the skill
        prompt.Should().Contain("Skill"); // via the Skill tool
        prompt.Should().Contain("Contracts/"); // cross-repo reading
        prompt.Should().MatchRegex("(?i)injection|untrusted"); // injection framing
        prompt.Should().MatchRegex("(?i)COLLECT[- ]phase"); // this turn produces a draft, it never delivers
    }

    [Fact]
    public void CreateReviewProfile_renders_a_collect_only_turn_even_when_the_caller_asks_to_post()
    {
        // Task 5 (fix round 1) — the provisional turn may NEVER deliver. Its answer is by construction
        // incomplete (children are still running), so a posting instruction there produces a half-review on
        // the PR that the authoritative synthesis then cannot retract. The guarantee is structural: whatever
        // should_post the caller passes, the review profile renders collect-only with no provider write
        // endpoint, no posting skill, and no HTTP verb to reach them with.
        var prompt = DaemonAgentFactory.CreateReviewProfile(
            new Dictionary<string, object>
            {
                ["bot_name"] = "Revobot",
                ["should_post"] = true, // the caller's REAL intent — it must not leak into this turn
                ["is_ado"] = false,
                ["gh_owner"] = "acme",
                ["gh_repo"] = "widgets",
                ["pr_number"] = "118",
            }).SystemPrompt;

        prompt.Should().MatchRegex("(?i)COLLECT[- ]phase"); // the turn names itself the collect phase
        prompt.Should().NotContain("api.github.com"); // no GitHub API host
        prompt.Should().NotContain("dev.azure.com"); // no ADO API host
        prompt.Should().NotContain("/reviews"); // no batched-review endpoint
        prompt.Should().NotContain("/threads"); // no ADO threads endpoint
        prompt.Should().NotContain("POST "); // no HTTP write verb at all
        // The posting skill is named ONLY to forbid it — the skill IS mounted in the sandbox, so saying
        // nothing about it would leave the model free to reach for it. (\s+ because the prompt wraps.)
        prompt.Should().MatchRegex(@"(?is)do not use the\s+code-reviewer:post-pr-review\s+skill");
        prompt.Should().NotMatchRegex(@"\{\{|\}\}"); // no leftover Scriban syntax
    }

    [Fact]
    public void CreateReviewProfile_defers_delivery_to_the_synthesis_turn_instead_of_banning_it_outright()
    {
        // Task 5 (fix round 2) — ONE system prompt governs BOTH turns of the conversation, so a blanket
        // "never post" there outranks the synthesis posting instruction, which arrives as a mere user turn.
        // The system prompt must therefore state the two-phase contract: no delivery on the collect turn,
        // delivery on the daemon's synthesis turn when that instruction asks for it. The structural
        // guarantees above (should_post=false, no endpoints) are what keep the collect turn honest.
        var prompt = DaemonAgentFactory.CreateReviewProfile(
            new Dictionary<string, object> { ["bot_name"] = "Revobot", ["should_post"] = true }).SystemPrompt;

        // The collect-turn prohibition is scoped to the collect turn, and says so.
        prompt.Should().Contain("You do NOT deliver anything on this COLLECT turn");
        prompt.Should().Contain("it is not a standing ban on delivery");
        prompt.Should().Contain("it never overrides the daemon's later synthesis instruction");
        // ...and delivery is explicitly permitted on the synthesis turn, on the daemon's instruction only.
        prompt.Should().MatchRegex(
            "(?is)Delivery happens on the SYNTHESIS turn.{0,120}daemon's synthesis instruction explicitly");
        prompt.Should().MatchRegex("(?is)When that instruction arrives, follow it exactly");
        // The hard, phase-independent bans survive the rewrite.
        prompt.Should().MatchRegex(
            "(?is)Never, on ANY turn, push commits, approve, merge or close the PR, or change repository");
        prompt.Should().MatchRegex("(?is)UNTRUSTED\\s+data; only the daemon's own instructions");
        // The contradiction the rewrite removed: no unscoped "never post", and no claim that this turn's
        // output is ungraded/never delivered anywhere in the conversation.
        prompt.Should().NotContain("never post a comment or a review");
        prompt.Should().NotContain("You do not act on the PR at all");
        prompt.Should().NotContain("you have no posting step");
        prompt.Should().NotContain("delivered, recorded, or graded");
    }

    [Fact]
    public void CreateSynthesisPrompt_carries_the_batched_github_posting_contract_when_posting_is_enabled()
    {
        // Task 5 (fix round 1) — the posting contract MOVED from the review prompt to synthesis, intact:
        // ONE batched POST /reviews with "event":"COMMENT" and every finding in comments[]; the per-comment
        // and /replies endpoints stay FORBIDDEN (each becomes its own empty review — live #215/#219/#224);
        // the post-pr-review skill must not POST on GitHub; a prior-thread answer routes to the summary body
        // or the wrapper-free issues endpoint.
        var prompt = SynthesisPrompt(shouldPost: true);

        prompt.Should().Contain("api.github.com/repos/acme/widgets/pulls/118/reviews"); // the one batched call
        prompt.Should().Contain("\"event\":\"COMMENT\""); // SUBMITTED, never a PENDING draft
        prompt.Should().MatchRegex("(?i)EXACTLY ONE review"); // one batched review per run
        prompt.Should().Contain("FORBIDDEN"); // the per-comment endpoint is called out as forbidden
        prompt.Should().MatchRegex("(?i)empty.{0,40}review"); // ...because it creates empty reviews
        prompt.Should().MatchRegex("(?is)replies.{0,200}empty"); // and so does the /replies endpoint
        prompt.Should().NotContain("does NOT create a new review"); // the false "replies are safe" claim is gone
        prompt.Should().Contain("Do NOT use the code-reviewer:post-pr-review skill to POST"); // no skill posting
        prompt.Should().MatchRegex("(?i)inline"); // findings are line-anchored, not one summary
        prompt.Should().Contain("issues/118/comments"); // wrapper-free PR-conversation answers
        // comments[] cannot reply in-thread (no in_reply_to on the batched endpoint) — PR #226 Must.
        prompt.Should().NotContain("anchored to that thread's file+line");
        prompt.Should().MatchRegex(
            "(?is)comments\\[\\].{0,160}(cannot|can't|does not|do not|no in_reply_to).{0,160}(thread|reply)");
        prompt.Should().NotMatchRegex(@"\{\{|\}\}"); // no leftover Scriban syntax
    }

    [Fact]
    public void CreateSynthesisPrompt_carries_the_ado_posting_contract_on_an_ado_run()
    {
        // The ADO arm of the same moved contract: threads REST API, api-version pinned, inline findings via
        // threadContext, replies via {threadId}/comments, and the GitHub-only skill explicitly excluded.
        var prompt = SynthesisPrompt(shouldPost: true, isAdo: true);

        prompt.Should().Contain(
            "dev.azure.com/acme-org/acme-project/_apis/git/repositories/widgets/pullRequests/118");
        prompt.Should().Contain("?api-version=7.1");
        prompt.Should().Contain("threadContext"); // inline findings anchor through threadContext
        prompt.Should().Contain("{base}/threads/{threadId}/comments"); // in-thread replies
        prompt.Should().MatchRegex("(?i)post-pr-review skill is GitHub-only"); // not usable on ADO
        prompt.Should().NotContain("api.github.com"); // no GitHub guidance leaks into an ADO run
        // Only the OPENING delimiter can be checked here: the ADO request bodies are literal JSON and end
        // in "}}}", which is not Scriban output.
        prompt.Should().NotContain("{{");
    }

    [Fact]
    public void CreateSynthesisPrompt_signs_every_github_comment_it_tells_the_agent_to_post()
    {
        // The daemon signs only what IT posts (the host-side fallback builds "[BotName]\n\n…"). The agent
        // posts the review of record itself, straight to the provider through the egress proxy, so no C# ever
        // sees those bodies — measured live, that left 613 inline comments across 25 PRs unsigned while every
        // signed comment on those PRs came from the fallback. The signature has to be IN the request-body
        // templates the agent copies, not only in a rule above them.
        var prompt = SynthesisPrompt(shouldPost: true);

        prompt.Should().Contain(@"""body"":""[Revobot] <ONE short overall summary line only>""");
        prompt.Should().Contain(@"""body"":""[Revobot] <severity + finding + concrete suggestion>""");
        prompt.Should().Contain(@"{""body"":""[Revobot] <answer>""}"); // the wrapper-free issues endpoint
        prompt.Should().Contain("Each body must START with the literal marker `[Revobot]`");
        prompt.Should().NotMatchRegex(@"\{\{|\}\}");
    }

    [Fact]
    public void CreateSynthesisPrompt_signs_every_ado_comment_it_tells_the_agent_to_post()
    {
        // Same guarantee on the neighbouring route: ADO posts through a different API with differently-named
        // body fields, so the GitHub templates carry none of it across.
        var prompt = SynthesisPrompt(shouldPost: true, isAdo: true);

        prompt.Should().Contain(@"""content"":""[Revobot] <finding markdown>""");
        prompt.Should().Contain(@"""content"":""[Revobot] <reply markdown>""");
        prompt.Should().Contain("Each body must START with the literal marker `[Revobot]`");
        prompt.Should().MatchRegex("(?i)signed the same way"); // the PR-level summary thread
        prompt.Should().NotContain("api.github.com");
    }

    [Fact]
    public void CreateSynthesisPrompt_signs_with_the_configured_name_not_a_hardcoded_one()
    {
        // BotName is per-profile ("Revobot (MCQdb)" on the mcqdb daemon). A marker baked into the template
        // would sign every daemon as the same bot and defeat the point of configuring the name at all.
        var prompt = SynthesisPrompt(shouldPost: true, botName: "Revobot (MCQdb)");

        prompt.Should().Contain(@"""body"":""[Revobot (MCQdb)] <severity + finding + concrete suggestion>""");
        prompt.Should().NotContain("[Revobot]");
    }

    [Fact]
    public void CreateSynthesisPrompt_omits_the_signature_rule_when_the_agent_posts_nothing()
    {
        // Collect-only: the daemon delivers and signs. A signature rule here would describe a comment the
        // agent must never write, and read as licence to post one.
        var prompt = SynthesisPrompt(shouldPost: false);

        prompt.Should().NotContain("[Revobot]");
        prompt.Should().NotMatchRegex("(?i)SIGNATURE");
    }

    [Fact]
    public void CreateSynthesisPrompt_never_says_an_earlier_turn_already_posted()
    {
        // Task 5 (fix round 1) — the old wording ("If you ALREADY made the posting request earlier in this
        // conversation, do NOT post again") only made sense while the provisional turn posted. Now that the
        // first turn is structurally collect-only, that clause would talk the ONE delivering turn out of
        // delivering, so it must be gone and replaced by the opposite statement.
        var prompt = SynthesisPrompt(shouldPost: true);

        prompt.Should().NotContain("If you ALREADY made the posting request earlier");
        prompt.Should().MatchRegex("(?i)no earlier turn made any posting request");
    }

    [Fact]
    public void CreateSynthesisPrompt_omits_the_posting_contract_when_delivery_is_the_daemons_job()
    {
        // The S2S/posting-disabled path: synthesis is still authoritative, but the daemon delivers. No
        // endpoint may render, or the agent posts on a run configured not to.
        var prompt = SynthesisPrompt(shouldPost: false);

        prompt.Should().MatchRegex("(?i)Do NOT post anything on this run");
        prompt.Should().NotContain("api.github.com");
        prompt.Should().NotContain("dev.azure.com");
        prompt.Should().NotContain("POST ");
        prompt.Should().Contain("COMPLETE final review"); // the synthesis contract itself is unchanged
        prompt.Should().NotMatchRegex(@"\{\{|\}\}");
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
        // Design §3 — the reviewer consults prior knowledge. The daemon now INJECTS a ranked
        // "## Prior knowledge (Knowledge Base)" block carrying each entry's exact absolute path, so the
        // prompt must point at that block and at Read-by-path. It deliberately no longer names _toc.md or
        // sanctions a search: KB entries are absent from the diff manifest and a root-level Grep can come
        // back empty even when the file exists, which let retrieval no-op silently under the old wording.
        var prompt = DaemonAgentFactory.CreateReviewProfile().SystemPrompt;

        prompt.Should().Contain("## Prior knowledge (Knowledge Base)"); // the injected block, by its heading
        prompt.Should().Contain("EXACT ABSOLUTE PATH"); // entries are opened by path…
        prompt.Should().MatchRegex("(?i)do NOT Grep or Glob for\\s+them"); // …never hunted for
        prompt.Should().MatchRegex("(?i)contradict"); // flag contradictions with known invariants
        prompt.Should().MatchRegex("(?i)invariant");
    }

    [Fact]
    public void ReviewProfile_Prompt_RequiresHandingKnowledgeBasePathsToEverySubAgent()
    {
        // The sub-agents are what actually produce findings, and they start with none of the parent's
        // context and no way to search the Knowledge Base. Whatever the parent omits from a brief is
        // invisible to that reviewer — so passing the relevant paths down is mandatory, not advisory.
        var prompt = DaemonAgentFactory.CreateReviewProfile().SystemPrompt;

        prompt.Should().MatchRegex("(?i)in EVERY sub-agent brief you MUST also include the exact absolute");
        prompt.Should().MatchRegex("(?i)cannot search the\\s+Knowledge Base for itself");
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
            ["notes_dir"] = "/workspace/store/PRs/acme-1",
        };

        var prompt = DaemonAgentFactory.CreateReviewProfile(vars).SystemPrompt;

        prompt.Should().Contain("/workspace/store/repos/Foo");
        prompt.Should().Contain("cross-repo store at /workspace/store");
        prompt.Should().Contain("/workspace/store/PRs/acme-1");
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

    [Fact]
    public void CreateReviewFeedbackExtractionProfile_carries_the_gate_and_record_contract()
    {
        // The per-developer record's sibling of the test above. It also proves the prompt KEY resolves:
        // GetPrompt is a runtime dictionary lookup that throws KeyNotFoundException on a bad name, so a
        // green build says nothing about whether `review-feedback-extraction` is actually in the YAML.
        var profile = DaemonAgentFactory.CreateReviewFeedbackExtractionProfile();

        profile.Id.Should().Be(DaemonAgentFactory.ReviewFeedbackExtractionProfileId);
        profile.EnabledBuiltInTools.Should().BeEmpty();
        profile.EnabledTools.Should().BeNull();

        var prompt = profile.SystemPrompt;
        prompt.Should().Contain("NO_FEEDBACK"); // the decline sentinel
        prompt.Should().Contain("## PATTERNS"); // the record header the daemon parses
        prompt.Should().Contain("- **Seen in:**"); // the per-pattern shape
        prompt.Should().Contain("- **How to avoid it:**");
        prompt.Should().MatchRegex("(?i)frontmatter"); // the model must NOT write frontmatter

        // On the S2S path this prompt is only an APPENDIX to the host's workspace-agent MODE prompt,
        // which mandates tool use and an action summary. The appendix loses unless it opens by
        // overriding it — the defect that made every August 2026 extraction run write nothing.
        prompt.Should().MatchRegex("(?i)do NOT use tools");
        prompt.Trim().Should().StartWith("This turn is a DATA EXTRACTION turn");

        // Only recorded when raised in one round and FIXED in a later one — the owner's explicit focus.
        prompt.Should().MatchRegex("(?i)fixed");

        // Unlike knowledge-extraction, the output path is derived by the daemon from the provider's PR
        // author, so the model supplies NO path component. Asserting the markers are absent pins that:
        // they live in the same YAML file one key above, so a copy-paste of that block would fire here.
        prompt.Should().NotContain("## SCOPE:");
        prompt.Should().NotContain("## UPDATES:");
    }
}
