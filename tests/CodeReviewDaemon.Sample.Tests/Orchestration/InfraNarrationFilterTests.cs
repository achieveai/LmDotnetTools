using CodeReviewDaemon.Sample.Orchestration;
using static CodeReviewDaemon.Sample.Orchestration.InfraNarrationFilter;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Every case here is either a real sentence pulled from a completed review in the #113 fixture corpus
/// (window 2026-08-06T22:13:36Z–2026-08-10T05:41:27Z, run ids cited in each test name/doc comment) or,
/// where noted, a real sentence relocated under a synthetic heading to isolate one mechanism (heading
/// scoping) from the vocabulary matching it would otherwise also need. The point of this file is the
/// negative controls: two structural anchors per category exist specifically because a keyword-only
/// filter would eat real author-facing content (an HTTP-status finding, a retry-policy discussion, a
/// migration's compatibility note) that merely shares words with infra narration. Every negative control
/// below is a sentence that DOES share vocabulary with one of the two categories and must still survive
/// untouched.
/// </summary>
public sealed class InfraNarrationFilterTests
{
    // ---- Negative controls: 8 real sentences that must survive byte-for-byte -----------------------------
    // Each looks, at a glance, like it could be infra narration. Each fails the two-anchor requirement (or,
    // for #5, matches no vocabulary at all) and so is never touched — this is the load-bearing property of
    // the whole filter, and it is what the "broaden to plain keywords" mutation below must break.

    [Fact]
    public void Filter_Run44_PostingVerbIsWrongForm_Survives()
    {
        // "present" is not "posted"/"made"/"modified" — PostingStatePattern does not match.
        const string Body =
            "## Verdict: REQUEST CHANGES\n\n" + "No new comments were present since the previous review.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run277_ExecutionBlockedButNoEnvironmentNoun_Survives()
    {
        // "did not run" matches ExecutionBlockedPattern, but "a local build or test suite" names no
        // sandbox/environment noun — the second anchor is what keeps this sentence (and the CI-evidence
        // sentence right before it) untouched.
        const string Body =
            "The supplied CI evidence records build **39044583** as completed and successful, "
            + "with **8,049 tests total, 8,038 passed, and 0 failed**. "
            + "I did not run a local build or test suite.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run287_PolarityMismatch_AvailableNotNotAvailable_Survives()
    {
        // The text says "are available", not "not available" — literal-polarity match only.
        const string Body = "Therefore, no compilation or test results are available as validation evidence.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run287_HasNotStartedIsNotACoveredVerbForm_Survives()
    {
        // "has not started" is not among the covered auxiliary forms (could not/did not/were not/was
        // not/no...were|was run/not installed|found|available/unavailable).
        const string Body =
            "The supplied pipeline record states that the build is queued and has not started.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run287_RetryPolicyFindingSharesNoVocabulary_Survives()
    {
        // The doc comment's own cited example: an author's finding about a retry policy. Matches nothing in
        // either category — not because of heading scoping (this sentence carries no heading context here
        // at all), but because "recurse into another retry" trips no covered verb or provider/posting
        // phrase.
        const string Body = "429 responses recurse into another retry without checking `init.signal`.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run240_PostingNounsAndVerbBothWrong_Survives()
    {
        // Neither "retractions"/"findings" match the comments|mutations noun alternation, nor does "were
        // present" match posted|made|modified.
        const string Body = "No sub-agent retractions or unsubstantiated findings were present.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run264_UnavailableWithNoEnvironmentNoun_Survives()
    {
        // "unavailable" alone satisfies ExecutionBlockedPattern's bare form, but nothing here names a
        // sandbox/checkout/toolchain/dotnet/npm/msbuild/jest — the second anchor still gates it out even
        // with no heading in play.
        const string Body = "An unavailable or malformed authorization response must not grant upload capability.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run190_SubAgentFailureMatchesNoPattern_KnownGap_Survives()
    {
        // Known gap (b): a sub-agent context-window failure is real infra narration by any human reading,
        // but "failed" only participates in AccessBlockedPattern, which requires a provider-name token
        // first (ProviderReferencePattern) — there is none here. This documents the limitation rather than
        // hiding it: the sentence survives, unfiltered.
        const string Body =
            "The dedicated rollout-review sub-agent failed due to a context-window error; the "
            + "mixed-version compatibility analysis above was independently substantiated by the "
            + "schema-compatibility review and the checked-in base/head code.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
        moved.Should().BeEmpty();
    }

    // ---- Known gaps: two more real sentences that document limitations, distinct from the 8 above --------

    [Fact]
    public void Filter_Run227_TypeScriptToolingNotACoveredEnvironmentNoun_KnownGap_Survives()
    {
        // Known gap (a): "could not run" matches ExecutionBlockedPattern, but "the installed TypeScript
        // version" / "tsconfig" name no covered environment noun (sandbox/checkout/toolchain/dotnet/npm/
        // msbuild/jest) — TypeScript tooling failures are a real but currently unaddressed flavor.
        const string Body =
            "Type-check was attempted but could not run because the installed TypeScript version "
            + "rejects existing `tsconfig` options (`baseUrl` and `moduleResolution: node10`).";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run78_NonStandardPostingNouns_KnownGap_Survives()
    {
        // Known gap (c): explicitly about posting state, but "finding, summary, approval, or merge action"
        // are not the comments|mutations nouns PostingStatePattern covers.
        const string Body = "No new finding, summary, approval, or merge action was posted.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
        moved.Should().BeEmpty();
    }

    // ---- Heading scoping in isolation: real vocabulary-matching sentences relocated under a synthetic ----
    // ---- finding heading, to prove the heading gate itself (not vocabulary) is what protects them. -------

    [Fact]
    public void Filter_SandboxSentenceUnderBlockerHeading_IsNeverRewritten()
    {
        // Run 41's sentence, verbatim, would be rewritten at top level (see below) — placed here under a
        // "#### Finding 2 — HIGH" heading it is untouched, because IsFindingHeading gates classification
        // before either pattern is even evaluated.
        const string Body =
            "#### Finding 2 — HIGH\n\n"
            + "Focused tests could not be run because the sandbox does not have `dotnet` installed.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_PostingStateSentenceUnderCriticalHeading_IsNeverMoved()
    {
        // Run 9's sentence, verbatim, would be moved at top level (see below) — placed here under a
        // "#### Finding 1 — CRITICAL" heading it is untouched and nothing is recorded to the operator
        // channel.
        const string Body =
            "#### Finding 1 — CRITICAL\n\n"
            + "No provider comments were posted, per the collect-only instruction.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
        moved.Should().BeEmpty();
    }

    // ---- Sandbox/tooling: REWRITE, never delete -------------------------------------------------------

    [Fact]
    public void Filter_Run41_CouldNotBeRun_IsRewrittenGenerically()
    {
        const string Sentence =
            "Focused tests could not be run because the sandbox does not have `dotnet` installed.";
        const string Body = "### Verification\n\n" + Sentence;

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().NotContain("dotnet");
        filtered.Should().NotContain(Sentence);
        filtered.Should().Contain(
            "Local build/test execution was not possible for this review; no results from running the "
                + "code are reflected in this assessment.");
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run222_CouldNotRun_ChecksOutFailure_IsRewrittenGenerically()
    {
        const string Sentence =
            "Focused Jest tests could not run because dependencies are unavailable in the checkout: "
            + "`jest: not found`.";
        const string Body = "### Verification\n\n" + Sentence;

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().NotContain("jest: not found");
        filtered.Should().Contain(
            "Local build/test execution was not possible for this review; no results from running the "
                + "code are reflected in this assessment.");
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run160_BareUnavailable_NoNotAtAll_IsRewrittenGenerically()
    {
        // The third distinct verb-form: no "not" anywhere, just "is unavailable in the sandbox".
        const string Sentence =
            "The v2 build was attempted with `UseMiseV2=true`, but `dotnet` is unavailable in the "
            + "sandbox (`exit 127`).";
        const string Body = "## Verification notes\n\n" + Sentence;

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().NotContain("exit 127");
        filtered.Should().Contain(
            "Local build/test execution was not possible for this review; no results from running the "
                + "code are reflected in this assessment.");
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run228_Precedence_SandboxToolingWinsOverProviderVocabularyInSameSentence()
    {
        // This one sentence matches BOTH categories' anchors at once: ExecutionBlockedPattern ("could not
        // start") + EnvironmentReferencePattern ("dependency resolution"/"jest"), AND ProviderReferencePattern
        // ("azure artifacts") + AccessBlockedPattern ("failed"). SandboxTooling is checked first and wins —
        // the sentence's subject is test execution, so it is rewritten (kept, generic) rather than moved
        // (dropped outright). Followed by a fenced `502 policy_evaluation_failed` block one blank line
        // later, swept away with it (fence-suppression).
        const string Body =
            "### Verification\n\n"
            + "A focused Jest test run was attempted but could not start because dependency resolution "
            + "failed through the Azure Artifacts proxy with:\n\n"
            + "```text\n502 policy_evaluation_failed\n```\n\n"
            + "### Overall recommendation\n\n"
            + "Approve with the existing non-blocking localization comment.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().NotContain("Azure Artifacts");
        filtered.Should().NotContain("policy_evaluation_failed");
        filtered.Should().NotContain("```");
        filtered.Should().Contain(
            "Local build/test execution was not possible for this review; no results from running the "
                + "code are reflected in this assessment.");
        filtered.Should().Contain("Approve with the existing non-blocking localization comment.");
        moved.Should().BeEmpty("the sentence matched both categories' vocabulary, but SandboxTooling takes precedence");
    }

    [Fact]
    public void Filter_Run181_FenceDirectlyAfterRewrittenSentence_IsSweptAway()
    {
        const string Sentence =
            "Targeted tests could not be run because the review environment does not have `dotnet` "
            + "installed:";
        const string Body =
            "### Verification\n\n"
            + Sentence
            + "\n\n```text\n/bin/sh: dotnet: not found\n```\n\n"
            + "### Overall recommendation\n\nNo blocking issues found.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().NotContain("/bin/sh");
        filtered.Should().NotContain("```");
        filtered.Should().Contain("No blocking issues found.");
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_FenceNotAttachedToARewrittenSentence_IsUntouched()
    {
        // Contrast case for the two fence-suppression tests above: a fence that follows ordinary prose (no
        // preceding SandboxTooling rewrite) is opaque pass-through, same as any other fenced sample a real
        // finding might quote.
        const string Body =
            "#### Finding 1 — HIGH\n\n"
            + "The handler swallows the exception before it can be logged:\n\n"
            + "```csharp\ncatch (Exception) { }\n```";

        var (filtered, _) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
    }

    // ---- CI-evidence preservation: the substituted evidence in NEIGHBORING segments must survive intact --

    [Fact]
    public void Filter_Run256_CiBulletsSurviveWhileDisclaimerIsRewritten()
    {
        const string Body =
            "## Validation evidence\n\n"
            + "Per the PR's recorded CI results:\n\n"
            + "- Build `39200087`: **completed / succeeded**\n"
            + "- Tests: **3,136 total; 2,929 passed; 0 failed**\n\n"
            + "I did not run a local build or tests because the sandbox has no toolchain or network access.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Contain("- Build `39200087`: **completed / succeeded**");
        filtered.Should().Contain("- Tests: **3,136 total; 2,929 passed; 0 failed**");
        filtered.Should().NotContain("toolchain");
        filtered.Should().Contain(
            "Local build/test execution was not possible for this review; no results from running the "
                + "code are reflected in this assessment.");
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run283_SentenceSplittingKeepsSecondSentenceOfSameParagraph()
    {
        // The key sentence-splitting demonstration: ONE paragraph, first sentence rewritten, second sentence
        // (which introduces the CI evidence) kept verbatim — sentence granularity, not paragraph granularity.
        const string Body =
            "## Validation evidence\n\n"
            + "I did not build or test locally because the sandbox has no toolchain or network. The "
            + "supplied PR pipeline recorded:\n\n"
            + "- Build **39219348**: completed and succeeded.\n"
            + "- Tests: **3617 total, 3617 passed, 0 failed**.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Contain("The supplied PR pipeline recorded:");
        filtered.Should().NotContain("I did not build or test locally");
        filtered.Should().Contain("- Build **39219348**: completed and succeeded.");
        filtered.Should().Contain("- Tests: **3617 total, 3617 passed, 0 failed**.");
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run252_NoWereRunForm_CiBulletsAboveSurviveUntouched()
    {
        const string Body =
            "### CI evidence\n\n"
            + "- Build **39161710**: **completed / succeeded**\n"
            + "- Tests: **8,503 total; 8,502 passed; 0 failed**\n\n"
            + "No local build or test commands were run, consistent with the stated sandbox constraint.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Contain("- Build **39161710**: **completed / succeeded**");
        filtered.Should().Contain("- Tests: **8,503 total; 8,502 passed; 0 failed**");
        filtered.Should().NotContain("sandbox constraint");
        filtered.Should().Contain(
            "Local build/test execution was not possible for this review; no results from running the "
                + "code are reflected in this assessment.");
        moved.Should().BeEmpty();
    }

    [Fact]
    public void Filter_Run264_CiEvidenceSectionWithNoDisclaimerAtAll_IsFullyUntouched()
    {
        // Negative control for the whole-section case: real reviews sometimes cite CI evidence with no
        // accompanying sandbox/tooling disclaimer sentence at all. Nothing here should ever be touched.
        const string Body =
            "## CI evidence\n\n"
            + "- Build `39181243`: **completed / succeeded**\n"
            + "- Tests: **7,988 total**, **7,977 passed**, **0 failed**\n\n"
            + "All dispatched review agents completed; their overlapping stale-state findings were "
            + "consolidated above.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Be(Body);
        moved.Should().BeEmpty();
    }

    // ---- Provider/HTTP + posting-state: MOVE to the operator channel, never shown to the author ----------

    [Fact]
    public void Filter_Run9_PostingStatePrimaryForm_IsMovedWithPostingStateSubTag()
    {
        const string Sentence = "No provider comments were posted, per the collect-only instruction.";
        var (filtered, moved) = InfraNarrationFilter.Filter("## Review\n\n" + Sentence);

        filtered.Should().NotContain(Sentence);
        filtered.Should().NotContain("provider");
        moved.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new MovedNote(InfraCategory.ProviderOrPosting, "posting_state", "Review", Sentence));
    }

    [Fact]
    public void Filter_Run17_ShortestPostingStatePositive_IsMoved()
    {
        const string Sentence = "No comments were posted.";
        var (filtered, moved) = InfraNarrationFilter.Filter("### Review Coverage\n\n" + Sentence);

        filtered.Should().NotContain(Sentence);
        moved.Should().ContainSingle().Which.SubTag.Should().Be("posting_state");
    }

    [Fact]
    public void Filter_Run54_ProviderHttpOnly_OneLongSemicolonSentence_IsMoved()
    {
        // Semicolons and parentheses do not split a sentence — this is one MovedNote, not several.
        const string Sentence =
            "Azure DevOps posting was unavailable during the run (`502 policy_evaluation_failed`; ADO "
            + "MCP startup also encountered npm `403 Forbidden`), so this review is delivered here only.";
        var (filtered, moved) = InfraNarrationFilter.Filter("## Review Coverage Notes\n\n" + Sentence);

        filtered.Should().NotContain("policy_evaluation_failed");
        moved.Should().ContainSingle();
        moved[0].SubTag.Should().Be("provider_http");
        moved[0].Text.Should().Be(Sentence);
    }

    [Fact]
    public void Filter_Run64_ProviderHttpAndPostingStateCombinedInOneSentence_IsMovedOnce()
    {
        const string Sentence =
            "The delegated Azure DevOps thread-reply action could not retrieve or update the existing "
            + "thread because the ADO request failed with HTTP 502 (`policy_evaluation_failed`); no "
            + "provider comment was posted.";
        var (filtered, moved) = InfraNarrationFilter.Filter(
            "### Review Coverage\n\n"
                + "The local PR diff and relevant surrounding code were reviewed.\n\n"
                + Sentence);

        filtered.Should().Contain("The local PR diff and relevant surrounding code were reviewed.");
        filtered.Should().NotContain("policy_evaluation_failed");
        moved.Should().ContainSingle().Which.SubTag.Should().Be("provider_http+posting_state");
    }

    [Fact]
    public void Filter_Run77_TwoDistinctSubtagsFromOneParagraph_ProduceTwoMovedNotes()
    {
        // Two real sentences, one paragraph: the first is provider_http only (its execution-blocked-looking
        // verb, "could not create", is not a covered ExecutionBlockedPattern verb, so SandboxTooling never
        // fires despite "sandbox" appearing in the text); the second is posting_state only ("mutation").
        const string Body =
            "### Posting status\n\n"
            + "The Azure DevOps posting sub-agent could not create the new comment because all ADO "
            + "requests failed at the sandbox egress proxy with HTTP 502 (`policy_evaluation_failed`). "
            + "No provider mutation was made.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().NotContain("policy_evaluation_failed");
        filtered.Should().NotContain("mutation");
        moved.Should().HaveCount(2);
        moved[0].SubTag.Should().Be("provider_http");
        moved[1].SubTag.Should().Be("posting_state");
        moved.Should().OnlyContain(n => n.Heading == "Posting status");
    }

    [Fact]
    public void Filter_Run93_ModifiedVerbVariant_IsMovedWithPostingStateSubTag()
    {
        const string Body =
            "The required in-thread reply could not be delivered: Azure DevOps requests failed with "
            + "`HTTP 502 policy_evaluation_failed`, and the ADO MCP package was unavailable due to a "
            + "registry `403`. No provider-side comments were modified.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        moved.Should().HaveCount(2);
        moved[0].SubTag.Should().Be("provider_http");
        moved[1].SubTag.Should().Be("posting_state");
        moved[1].Text.Should().Be("No provider-side comments were modified.");
        filtered.Should().NotContain("comments were modified");
    }

    [Fact]
    public void Filter_Run221_ProviderTokenPresentButAccessVerbAbsent_ClassifiesAsPostingStateOnly()
    {
        // "were posted" does not match AccessBlockedPattern, so providerHit is false here even though an
        // ado-adjacent provider reference appears in the sentence; only postingHit fires.
        const string Sentence = "No provider/API comments were posted in this collect-only review";
        var (filtered, moved) = InfraNarrationFilter.Filter("## Summary\n\n" + Sentence + ".");

        filtered.Should().NotContain("provider/API");
        moved.Should().ContainSingle().Which.SubTag.Should().Be("posting_state");
    }

    [Fact]
    public void Filter_Run235_MoveThenKeep_SentenceSplittingInsideOneParagraphUnderPlainHeading()
    {
        // "### Final Recommendation" is a plain (non-finding) heading. Same paragraph, two sentences: the
        // first is a pure posting-state disclosure (moved), the second is real, author-facing content about
        // outstanding findings (kept) — this is what sentence-level (not paragraph-level) granularity
        // exists for.
        const string Body =
            "### Final Recommendation\n\n"
            + "No new comment should be posted from this review. The PR still requires resolution or "
            + "explicit disposition of the existing active findings listed above before approval.";

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().NotContain("No new comment should be posted");
        filtered.Should().Contain(
            "The PR still requires resolution or explicit disposition of the existing active findings "
                + "listed above before approval.");
        moved.Should().ContainSingle().Which.SubTag.Should().Be("posting_state");
    }

    [Fact]
    public void Filter_Run240_TrailingPositiveAtDocumentEnd_IsMoved()
    {
        const string Sentence = "No additional review comment should be posted.";
        var (filtered, moved) = InfraNarrationFilter.Filter(
            "No sub-agent retractions or unsubstantiated findings were present.\n\n" + Sentence);

        // The negative-control sentence right before it (Filter_Run240_..._Survives above) is untouched.
        filtered.Should().Contain("No sub-agent retractions or unsubstantiated findings were present.");
        filtered.Should().NotContain(Sentence);
        moved.Should().ContainSingle().Which.SubTag.Should().Be("posting_state");
    }

    // ---- Bullet-level granularity: a real finding bullet and a real sandbox-narration bullet, adjacent, --
    // ---- under one shared heading that names no severity ---------------------------------------------------

    [Fact]
    public void Filter_AdjacentFindingAndSandboxBullets_UnderOneSharedPlainHeading_OnlySandboxBulletIsRewritten()
    {
        // No corpus review mixes a genuine finding and infra narration as adjacent bullets under one heading
        // (checked: zero such cases across all 261 completed #113-window reviews), so this composes two real
        // pieces rather than quoting one run. The heading is run 245's actual heading verbatim ("## Review
        // findings" — a flat bullet-list finding style with no per-bullet severity heading of its own, so
        // IsFindingHeading does NOT exempt this section). The first bullet is run 245's actual MEDIUM finding,
        // verbatim. The second bullet is run 41's actual sandbox sentence, verbatim, reformatted onto a bullet
        // line rather than a bare paragraph. If this filter operated at section or heading granularity rather
        // than per-bullet, either both bullets would be left alone (the sandbox sentence leaks to the author)
        // or both would be touched (the real finding is corrupted or dropped) — BulletLine's per-line
        // segmentation is what keeps them independent.
        const string FindingBullet =
            "- **[MEDIUM]** `Sources/AutomationTests/Tests/Analysis/Analysis-CreateAnalysisTests.spec.ts:714-719` "
            + "`selectCategoryFilter` converts every `visibleTab.click()` failure into `false` and then treats "
            + "the category as an overflow-menu case. This masks genuine failures such as a detached element, "
            + "navigation failure, or browser error, producing a misleading overflow-menu timeout. Only a "
            + "confirmed overflow condition should use the fallback; unexpected click errors should propagate.";
        const string SandboxBullet =
            "- Focused tests could not be run because the sandbox does not have `dotnet` installed.";
        const string Body = "## Review findings\n\n" + FindingBullet + "\n" + SandboxBullet;

        var (filtered, moved) = InfraNarrationFilter.Filter(Body);

        filtered.Should().Contain(
            "`selectCategoryFilter` converts every `visibleTab.click()` failure into `false`",
            "the genuine finding bullet sits under the very same heading as the sandbox bullet and must "
                + "survive untouched — this is the property section/heading-level filtering could not give");
        filtered.Should().Contain(
            "Only a confirmed overflow condition should use the fallback; unexpected click errors should "
                + "propagate.",
            "the finding's remedy sentence is not truncated or otherwise disturbed");
        filtered.Should().NotContain("dotnet");
        filtered.Should().Contain(
            "Local build/test execution was not possible for this review; no results from running the "
                + "code are reflected in this assessment.",
            "the adjacent sandbox bullet is still rewritten (never deleted) even though it directly follows "
                + "a real finding bullet with no blank line between them");
        moved.Should().BeEmpty();
    }
}
