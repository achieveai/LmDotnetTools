# P6 · Optimization & Evaluation Engine

Epic: #298. Slices: #319 (harness) → #320 (eval runner) → #321 (experiment record) → #322 (routing cascade) → #323 (human-edit feedback).

## 0. Why this pillar exists

The product sells governed autonomous agents priced per outcome. The margin comes from a loop: an
agent produces an outcome, a human reviews and edits it, the correction accrues as durable context,
and A/B optimization moves cheap gathering work onto cheap models while reserving expensive models
for judgment. Every step of that loop is a measurement claim. Without a shared, trustworthy
measurement substrate, "the new prompt is better" is an anecdote and "Haiku is good enough here" is a
guess.

P6 is that substrate. It is deliberately *not* a new agent framework — it is the small, boring,
heavily-tested piece that turns an agent output into a defensible number.

## 1. Scope and non-goals

### In scope

- One shared **judge harness** (`src/LmEval`) implementing the gauntlet shape:
  **deterministic gate → rubric judge → panel vote**.
- Migrating Revobot's existing guardrails onto that harness with **no behaviour change**.
- An **eval runner** that replays a recorded corpus and reports against a per-task-type baseline.
- An **experiment record** joining a variant's judge score to its measured cost.
- A **routing cascade** whose thresholds are fitted from the experiment record, not hand-tuned.
- A **human-edit feedback** path turning a reviewer's correction into a durable context entry.

### Non-goals

- **Not a training pipeline.** No fine-tuning, no weight updates. "Training signal" in §8 means
  retrieval context, not parameters.
- **Not a replacement for `LmWorkflow`.** Orchestration of a *workflow* stays there. See §2.11.
- **Not an online gate on Revobot posting.** Judge verdicts stay advisory exactly as today
  (`samples/CodeReviewDaemon.Sample/Agents/JudgeAgent.cs:12`, and the prompt itself says so at
  `samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml:357`). Promoting a verdict to a gate
  is a separate, later decision (Q7).
- **Not a human-labelling product.** We consume human verdicts the daemon already collects; we do not
  build an annotation UI.
- **No new provider abstractions.** Judges are driven through the existing `IMultiTurnAgent` seam.

## 2. The harness abstraction

### 2.1 Where it lives, and why

New project **`src/LmEval`** → assembly `AchieveAi.LmDotnetTools.LmEval`, tests in
`tests/LmEval.Tests`, both registered in `LmDotnetTools.sln`.

It must depend on **`LmCore` + `LmMultiTurn` only**. That is forced by the reference graph:

| Project | References today |
| --- | --- |
| `src/LmWorkflow/AchieveAi.LmDotnetTools.LmWorkflow.csproj` | `LmCore`, `LmMultiTurn` |
| `src/LmAgentInfra/AchieveAi.LmDotnetTools.LmAgentInfra.csproj` | `LmCore`, `LmLifecycle`, `LmMultiTurn`, `Sandbox` |
| `samples/CodeReviewDaemon.Sample/CodeReviewDaemon.Sample.csproj` | `LmAgentInfra`, `LmSampleShared`, `GithubCopilotProvider` |

The daemon does **not** reference `LmWorkflow`, and `LmWorkflow` does **not** reference
`LmAgentInfra` — they are siblings over a shared `LmMultiTurn`. So the harness cannot live in either
without a cycle or an unwanted dependency. A leaf project over `LmCore` + `LmMultiTurn` is reachable
from both, and from a future eval runner, with no cycle.

`LmMultiTurn` (not just `LmCore`) is required because the harness drives judges through
`IMultiTurnAgent` and attributes cost through `IUsageSink`
(`src/LmMultiTurn/UsageAccounting/IUsageSink.cs:11`).

### 2.2 Core vocabulary

Four nouns. Each is a record; each is independently testable; none knows about code review.

```
Candidate   the thing being judged, plus the task it answers
Gate        a deterministic, LLM-free predicate over a Candidate
Rubric      a versioned, anchored scoring contract
Judge       something that turns (Candidate, Rubric) into a Ballot
Verdict     the aggregated decision over N Ballots
```

### 2.3 Candidate and task identity

```csharp
namespace AchieveAi.LmDotnetTools.LmEval;

/// <summary>One thing to be judged. TaskType is the baseline partition key (§5): scores are
/// compared only within a task type, never across.</summary>
public sealed record Candidate
{
    public required string CandidateId { get; init; }
    public required string TaskType { get; init; }
    /// <summary>The task as posed — the prompt/diff/question the candidate answers.</summary>
    public required string TaskInput { get; init; }
    public required string Content { get; init; }
    /// <summary>Optional independently-produced reference answer. The single largest accuracy
    /// lever available to a judge (§3.4).</summary>
    public string? Reference { get; init; }
    /// <summary>Which arm produced this candidate. Null for a corpus item with no variant.</summary>
    public string? VariantId { get; init; }
    public string? ModelId { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>();
}
```

`TaskType` is load-bearing: **a score is meaningful only relative to other scores of the same
`TaskType`.** A 6/10 code review and a 6/10 summarization are not comparable quantities. The eval
runner enforces this by partitioning every baseline on it (§5.2).

### 2.4 Gate — the deterministic, free layer

A gate is a pure predicate that costs no tokens. It exists because the cheapest way to reject an
outright failure is to never ask a model about it, and because several judge biases (verbosity,
formatting, schema violation) are exactly the dimensions a deterministic check can settle outright
rather than defend against.

The shape deliberately mirrors the daemon's existing `OperationPolicy.Decide` → `PolicyDecision`
pair (`samples/CodeReviewDaemon.Sample/Workspace/OperationPolicy.cs:121`, `:44`, `:46`), which
already proved out `Allow(reason)` / `Deny(reason)` with a mandatory human-readable reason.

```csharp
public enum GateOutcome { Pass, Reject, Inconclusive }

public sealed record GateDecision(GateOutcome Outcome, string GateId, string Reason)
{
    public bool IsPass => Outcome == GateOutcome.Pass;

    public static GateDecision Pass(string gateId, string reason) => new(GateOutcome.Pass, gateId, reason);
    public static GateDecision Reject(string gateId, string reason) => new(GateOutcome.Reject, gateId, reason);
    /// <summary>The gate could not run (a tool missing, a checkout absent). NOT a pass and NOT a
    /// reject: it escalates to the judge and is recorded, so an infrastructure failure can never be
    /// mistaken for a clean bill of health.</summary>
    public static GateDecision Inconclusive(string gateId, string reason)
        => new(GateOutcome.Inconclusive, gateId, reason);
}

public interface IGate
{
    string GateId { get; }
    /// <summary>Task types this gate applies to; empty means all.</summary>
    IReadOnlySet<string> AppliesTo { get; }
    ValueTask<GateDecision> EvaluateAsync(Candidate candidate, CancellationToken cancellationToken);
}
```

Three-valued rather than boolean on purpose. A two-valued gate forces an infrastructure failure to be
encoded either as a pass (silently unchecked) or a reject (a false negative that looks like a real
finding). `Inconclusive` keeps that distinction visible in the record.

**Ordering.** Gates run in registration order; the first `Reject` short-circuits and no judge runs.
`Inconclusive` does not short-circuit but is carried into the verdict.

Prior art for gate-first evaluation: HumanEval's `pass@k` runs unit tests and no model judges
anything (Chen et al., 2021, arXiv:2107.03374); SWE-bench formalizes the two-gate pattern with
`FAIL_TO_PASS` (the fix works) plus `PASS_TO_PASS` (nothing regressed), both deterministic and run
before any semantic judgement (Jimenez et al., 2024, arXiv:2310.06770).

### 2.5 Rubric — the scoring contract

```csharp
/// <summary>One dimension of a rubric, with explicit anchors. Anchors are mandatory: an unanchored
/// integer scale is where score clustering and inter-judge disagreement come from.</summary>
public sealed record RubricCriterion
{
    public required string CriterionId { get; init; }
    public required string Description { get; init; }
    /// <summary>Score value -> what that value means. At minimum the floor, midpoint and ceiling
    /// must be described.</summary>
    public required IReadOnlyDictionary<int, string> Anchors { get; init; }
    public double Weight { get; init; } = 1.0;
}

public sealed record Rubric
{
    public required string RubricId { get; init; }
    /// <summary>Bumped on ANY text change. Scores from different rubric versions are never
    /// pooled — see §5.4.</summary>
    public required string RubricVersion { get; init; }
    public required string TaskType { get; init; }
    public required int MinScore { get; init; }
    public required int MaxScore { get; init; }
    public required IReadOnlyList<RubricCriterion> Criteria { get; init; }
    /// <summary>Score at or above which the candidate is acceptable. Fitted from the corpus
    /// (§5), not guessed.</summary>
    public required int PassThreshold { get; init; }
    public bool RequireReasoningBeforeScore { get; init; } = true;
}
```

Design choices and the evidence for each:

- **Anchored, per-criterion rubric rather than one global "quality" number.** Prometheus-13B with a
  custom 5-point rubric plus a reference answer reaches Pearson 0.897 with human evaluators across 45
  rubrics — level with GPT-4 (0.882), far above ChatGPT (0.392) (Kim et al., 2023, arXiv:2310.08491).
- **Reasoning before the score** (`RequireReasoningBeforeScore`). Chain-of-thought before the verdict
  cut GPT-4's math-grading failure rate from 70% to 30% (Zheng et al., 2023, arXiv:2306.05685). The
  harness enforces this structurally: the response schema puts `reasoning` before `score`, so the
  model cannot emit a score it has not yet justified.
- **Pointwise for gating, pairwise only for ranking.** Pairwise tracks human preference better
  (Liu et al., 2024, arXiv:2403.16950), but pairwise preferences flip ~35% of the time under
  distractor features versus ~9% for absolute scores (Tripathi et al., 2025, arXiv:2504.14716).
- **Integer scale.** G-Eval's probability-weighted scoring recovers resolution integers discard
  (Liu et al., 2023, arXiv:2303.16634) but needs token logprobs, which several providers behind
  `IMultiTurnAgent` do not expose. Recorded as Q3.

### 2.6 Judge and Ballot

```csharp
/// <summary>One judge's opinion on one candidate. A Ballot is a claim, not a decision.</summary>
public sealed record Ballot
{
    public required string JudgeId { get; init; }
    public required string ModelId { get; init; }
    /// <summary>Recorded so aggregation can enforce family disjointness and detect
    /// generator/judge collision (§3.2).</summary>
    public required string ModelFamily { get; init; }
    public required IReadOnlyDictionary<string, int> CriterionScores { get; init; }
    public required double WeightedScore { get; init; }
    public required string Reasoning { get; init; }
    /// <summary>Self-reported confidence in [0,1]. Below the abstain floor the ballot is recorded
    /// but excluded from the tally (§2.8).</summary>
    public required double Confidence { get; init; }
    /// <summary>True when the judge declined to score. An abstention is DISTINCT from a zero.</summary>
    public required bool Abstained { get; init; }
    public string? AbstainReason { get; init; }
    /// <summary>Which presentation order this ballot was cast under (§3.1).</summary>
    public required int PresentationSeed { get; init; }
    public required TokenCost Cost { get; init; }
}

/// <summary>New to LmEval. Cost in USD micro-units, matching UsageRecord's units exactly so no
/// conversion happens at the boundary. Provenance is non-null so an unpriced call is never read
/// as a free one (§6.2).</summary>
public sealed record TokenCost(long InputTokens, long OutputTokens, long ReasoningTokens,
    long? CostMicros, CostProvenance Provenance);

public interface IJudge
{
    string JudgeId { get; }
    string ModelId { get; }
    string ModelFamily { get; }
    Task<Ballot> JudgeAsync(Candidate candidate, Rubric rubric, JudgeContext context,
        CancellationToken cancellationToken);
}

/// <summary>Per-invocation controls the harness sets, not the judge implementation.</summary>
public sealed record JudgeContext
{
    public required int PresentationSeed { get; init; }
    public IReadOnlyList<Candidate> Peers { get; init; } = [];
    public string? Reference { get; init; }
}
```

**Abstention is a first-class outcome, not score 0.** Revobot's current judge conflates them:
`JudgeAgent.ParseVerdict` returns score `0` when the reply does not parse
(`samples/CodeReviewDaemon.Sample/Agents/JudgeAgent.cs:87` and `:110`), so a malformed response is
indistinguishable in the data from a genuine worst-possible review. That is a silent data-corruption
bug in every aggregate computed over those artifacts. `Abstained` + `Confidence` fixes it.

### 2.7 Verdict and aggregation

```csharp
public enum VerdictOutcome { Pass, Fail, Split, NoDecision }

/// <summary>What the panel actually ended up with. Non-null so a reader who ignores it cannot
/// mistake a one-judge verdict for a full-panel one (§2.12.6).</summary>
public enum PanelDegradation { None, SingleJudge, PanelUnavailable, ArbiterUnavailable }

public sealed record Verdict
{
    public required string CandidateId { get; init; }
    public required VerdictOutcome Outcome { get; init; }
    /// <summary>Aggregated score on the rubric's scale. Null when NoDecision.</summary>
    public double? Score { get; init; }
    public required IReadOnlyList<GateDecision> GateDecisions { get; init; }
    public required IReadOnlyList<Ballot> Ballots { get; init; }
    /// <summary>Ballots cast but excluded, each with why. Never silently dropped.</summary>
    public required IReadOnlyList<ExcludedBallot> ExcludedBallots { get; init; }
    /// <summary>Disagreement among counted ballots. High dispersion on a gating item is a
    /// review-this-by-hand signal.</summary>
    public required double Dispersion { get; init; }
    public required string RubricId { get; init; }
    public required string RubricVersion { get; init; }
    public required TokenCost TotalCost { get; init; }
    public required string TieBreakRule { get; init; }
    public required PanelDegradation Degradation { get; init; }
    /// <summary>Names the unreachable family when Degradation is not None. Stable, non-sensitive
    /// text only — same rail as GateDecision.Reason (§2.11).</summary>
    public string? DegradationReason { get; init; }
}

public sealed record ExcludedBallot(Ballot Ballot, string ExclusionReason);

public interface IBallotAggregator
{
    string RuleId { get; }
    Verdict Aggregate(Candidate candidate, Rubric rubric,
        IReadOnlyList<GateDecision> gates, IReadOnlyList<Ballot> ballots);
}
```

**Default aggregator: `WeightedMedianAggregator`.**

1. Drop every ballot with `Abstained == true` or `Confidence < HarnessOptions.AbstainFloor`
   (default `0.34`), recording each in `ExcludedBallots`.
2. If fewer than `HarnessOptions.MinCountedBallots` survive → `NoDecision`. **Not a fail.** An
   unmeasured candidate and a bad candidate are different facts. `MinCountedBallots` is **2 in
   gating mode and 1 in advisory mode**: advisory runs deliberately accept a single surviving
   ballot and mark the verdict `Degradation = SingleJudge` (§2.12.6), whereas a gating run refuses.
   A single-ballot verdict never reports a `Dispersion`.
3. With two counted ballots, apply the straddle test of §2.12.2 — same side of `PassThreshold`
   decides directly, opposite sides runs the tie-break of §2.12.3.
4. `Score` is the reliability-weighted mean of the counted ballots (§2.9); with one ballot it is
   that ballot's score. On an arbiter-resolved straddle it is the arbiter's score, not a blend.

**Weighted, not unweighted.** Weak-supervision-weighted ensembles of <=70B judges reach o3-mini-level
selection accuracy (87.7% avg), and weighting significantly beats unweighted aggregation
(Saad-Falcon et al., 2025, arXiv:2506.18203).

Panel composition and tie-breaking are the subject of §2.12, which is where the two-family
constraint is worked through.

### 2.8 Low confidence and abstention

Three separate channels, deliberately not collapsed:

| Signal | Meaning | Effect |
| --- | --- | --- |
| `Abstained = true` | judge refused to score at all | excluded, recorded |
| `Confidence < AbstainFloor` | judge scored but distrusts itself | excluded, recorded |
| `Dispersion > DispersionAlarm` | judges disagree with each other | verdict stands, flagged for human review |

The third is the panel-level analogue of the first two and feeds the human-review queue in §8.

### 2.9 Judge calibration

`JudgeReliability` is a per-`(JudgeId, TaskType, RubricVersion)` weight in `[0,1]`, fitted from
agreement with recorded human verdicts (§6). It defaults to `1.0` for an uncalibrated judge, so the
harness is usable on day one and improves as human verdicts accumulate. Fitting is slice #321 work,
not #319.

### 2.10 The pipeline

```csharp
public sealed class JudgeGauntlet
{
    public JudgeGauntlet(IReadOnlyList<IGate> gates, JudgePanel panel,
        IBallotAggregator aggregator, HarnessOptions options, IUsageSink? usageSink = null,
        ILogger<JudgeGauntlet>? logger = null);

    public Task<Verdict> RunAsync(Candidate candidate, Rubric rubric,
        CancellationToken cancellationToken);
}
```

`RunAsync` = run gates in order → short-circuit on first `Reject` (emitting a `Verdict` with
`Outcome = Fail`, no ballots, near-zero cost) → otherwise fan the panel out concurrently, one
`PresentationSeed` per judge → aggregate → emit. Cost is accumulated across every model call and
reported on the `Verdict` whether it passed or failed.

### 2.11 Relationship to `LmWorkflow`

The harness deliberately does **not** build on `LmWorkflow`'s node graph, for structural reasons.

`LmWorkflow` is a **controller-LLM-driven** state machine, not a deterministic executor. Nothing
auto-advances: `WorkflowRuntime.AdvanceTo` (`src/LmWorkflow/Runtime/WorkflowRuntime.cs:1211`) is
called by the controller model via the `SetCurrentNode` tool
(`src/LmWorkflow/Tools/WorkflowToolProvider.cs:98`), and the join state computed by
`WorkflowProjectionBuilder.BuildJoin` (`src/LmWorkflow/Runtime/WorkflowProjectionBuilder.cs:147`) is
**advisory** — its `satisfied` flag blocks nothing. Worse for our purposes, `JoinMode.Quorum`
(`src/LmWorkflow/Model/WorkflowEnums.cs:48`) is rejected outright by the validator
(`src/LmWorkflow/Ingest/WorkflowValidator.cs:175`) and `JoinPolicy.Threshold`
(`src/LmWorkflow/Model/JoinPolicy.cs:7`) is unused. Majority vote *is* a quorum join, so the one
primitive we most need is the one V1 does not have. There is also no reduce node — named as
explicitly out-of-V1 at `src/LmWorkflow/Model/WorkflowNode.cs:105` — and no reducer seam of any kind.

Putting a vote tally behind a controller LLM would also make aggregation itself non-deterministic,
which defeats the point of the artifact.

**What we reuse instead**, without depending on `LmWorkflow`:

- `IJsonSchemaValidator` (`src/LmCore/Utils/IJsonSchemaValidator.cs:11`) for the judge's structured
  output — the same validator `LmWorkflow` uses.
- The **validate → bounded retry → error marker** lifecycle, copying the shape of
  `TaskCoordinator.HandleFailure` (`src/LmWorkflow/Runtime/TaskCoordinator.cs:678`) and
  `WorkflowTask.MaxValidationRetries` (`src/LmWorkflow/Model/WorkflowTask.cs:57`).
- The PII rail documented at `src/LmWorkflow/Runtime/TaskCoordinator.cs:662` — an error string that
  reaches persistence must be stable and non-sensitive, never a raw payload or exception message.
  `GateDecision.Reason` and `ExcludedBallot.ExclusionReason` are held to the same rule.

**What we give back.** Once `IBallotAggregator` exists as a tested, deterministic reducer, it is the
natural implementation behind a future `LmWorkflow` reduce node and a real `JoinMode.Quorum`. That is
the third-consumer payoff, recorded as a follow-up rather than scoped here.

### 2.12 Panel composition: two families, and what that costs

**Decision: the harness ships a two-judge panel.** Three genuinely disjoint model families are not
reliably reachable in this deployment (Q2, now closed), so the three-judge PoLL shape is not
available and the design commits to two rather than pretending otherwise.

#### 2.12.1 Composition rule

`JudgePanel` requires exactly two judges with **distinct `ModelFamily` values**, neither equal to the
candidate's generator family (§3.2). Construction throws otherwise. `HarnessOptions.PanelSize` is
fixed at 2 for the default panel; `AllowSameFamilyPanel` remains available for deliberate experiments
and stamps the verdict so those rows are never pooled with clean ones.

#### 2.12.2 What "disagree" means on an ordinal scale

Two judges rarely produce identical scores, so raw score inequality is the wrong trigger — it would
fire on almost every candidate. What matters is whether they land on **opposite sides of
`Rubric.PassThreshold`**:

- **Same side** → the panel agrees on the decision even if the scores differ. `Score` is the mean of
  the two weighted scores; `Outcome` is that side. Recorded as `"consensus"`. This resolves the
  common case for free.
- **Opposite sides** → a **straddle**. This is the genuine disagreement, and only here does the
  tie-break ladder run.

`Dispersion` (§2.7) is still recorded in the same-side case: two judges agreeing on `Pass` at 9 and 6
agree on the decision but not on the quality, and that gap is a rubric-quality signal worth keeping.

#### 2.12.3 The tie-break rule (chosen)

On a straddle, in order, with the applied rule recorded verbatim in `Verdict.TieBreakRule`:

1. **Arbiter escalation**, if `HarnessOptions.ArbiterJudge` is configured, its family is not the
   generator's, and its tier is at or above the generator's (§7.2). One call. The arbiter's side
   decides; `Score` becomes the arbiter's weighted score, not a blend. Recorded as
   `"arbiter:<judgeId>:<family>"`.
2. **Otherwise `Split`** — a first-class `VerdictOutcome`, recorded as `"split:unresolved"`. Not a
   pass, not a fail, and not a `NoDecision` (which means *the panel could not be run*; `Split` means
   *the panel ran and the judges genuinely disagreed*).

The arbiter is a **stronger single model**, not a third peer. It costs one extra call on the straddle
rate only — if straddles run at 15%, the panel costs 2.15 calls per candidate, not 3.

**Why an arbiter rather than a third peer judge.** A third peer would necessarily share a family with
one of the two judges, because only two families are reachable. That converts a tie into a *family
vote*: whichever family holds two of the three seats wins by construction. It manufactures a majority
instead of measuring one, and it would do so invisibly — the verdict would read as a 2–1 consensus
when it is really one family outvoting another. An arbiter that is explicitly recorded as the
deciding voice, with its family named in `TieBreakRule`, keeps that visible.

**Why not confidence-weighted resolution.** Self-reported confidence is not calibrated across model
families. One family's 0.8 and another's 0.8 are not the same quantity, and we have no cross-family
calibration on day one — `JudgeReliability` defaults to 1.0 (§2.9). Picking the higher-confidence
judge would produce a decisive-looking number out of noise, which is the exact failure this pillar
exists to prevent. Confidence is still used, but only where it is meaningful: **within** a judge, as
the abstain floor (§2.8), and as recorded data for later calibration. Once §8.3 has fitted per-family
reliability from human verdicts, revisiting this becomes legitimate — a follow-up, not a day-one rule.

**Why `Split` is acceptable as a terminal outcome.** Verdicts are advisory in #319 (§1), so a `Split`
costs nothing operationally today. It is also the more useful datum: the **straddle rate is a direct
estimate of judge unreliability**, available without any human labels at all. A rising straddle rate
on a stable corpus says the rubric is underspecified at its threshold — precisely the diagnostic the
eval runner needs, and precisely what a forced resolution would erase. §5.3 therefore reports
straddle rate alongside `NoDecision` rate.

#### 2.12.4 What two judges cost in signal quality

Stated plainly so the decision is auditable later.

| Property | 3 disjoint families (PoLL) | 2 disjoint families (chosen) |
| --- | --- | --- |
| Majority exists | yes — a biased judge is outvoted | **no.** An even panel has no majority |
| Effect of one biased judge | absorbed, verdict still correct | surfaces as a straddle — *detected*, not corrected |
| Cross-family bias cancellation | partial | **none.** Two families cannot average out a shared bias |
| Cost per candidate | 3 calls | 2 calls + arbiter on the straddle rate |
| Disagreement is informative | weakly (2–1 vs 3–0) | **strongly** — the straddle rate is the primary reliability signal |

The honest summary: **we trade error *correction* for error *detection*.** PoLL's claim is that three
small disjoint judges beat one large judge with less intra-model bias and >7x lower cost
(Verga et al., 2024, arXiv:2404.18796); the mechanism is the majority. Without a majority we do not
get that correction. What we keep is the part that matters most early: two independent families
disagreeing is a reliable alarm that the rubric or the candidate is genuinely borderline, and that
alarm is what §5.4 and §8.3 consume.

This also means **§3.2's "a self-preferring judge is outvoted by construction" no longer holds**, and
it has been corrected there: with two judges such a judge is detected, not outvoted.

#### 2.12.5 When the generator's family is one of the only two

§3.2 excludes the generator's family from the panel. With only two families reachable, that exclusion
can leave one judge. The harness then runs the **single non-generator judge** and marks the verdict
`SingleJudge` (degraded, §2.12.6). It does **not** admit the generator's family to reach a count of
two.

The reason is specific to an even panel: **a compromised second judge is worse than a missing one.**
With two judges, agreement *is* the signal. A judge from the generator's family that self-prefers
will agree with the generator's output, producing **false consensus** — a `"consensus"` verdict that
reads as two independent families concurring when it is one model family agreeing with itself. That
does not merely add noise; it corrupts the single quantity the two-panel exists to produce. A
one-judge verdict is weaker but honest, and it is labelled.

The better fix is upstream: **constrain generator routing so the generator never occupies one of the
two judge families.** That is a model-selection constraint and belongs to #322 (§7.1), where routing
is already being decided; it is recorded there as a hard constraint on the cascade rather than a
preference.

#### 2.12.6 Degradation when a provider is down

`HarnessOptions.GatingMode` decides, because the right answer differs by consequence:

- **Advisory mode (the default, and all of #319):** fall back to the **single reachable judge**. The
  verdict is emitted with `Degradation = SingleJudge`, `Dispersion = null`, and a
  `DegradationReason` naming the unreachable family. It is a real verdict and is written to the
  experiment record, but §5 aggregates **exclude degraded rows by default** and report their count
  separately. One judge's read on a corpus item is worth more than a hole, provided the hole is
  labelled.
- **Gating mode:** **fail closed.** Emit `NoDecision` with `Degradation = PanelUnavailable`. A gate
  that silently weakens to one judge when a provider blips is exactly how a quality bar erodes
  without anyone deciding to lower it.
- **Both judges unreachable:** `NoDecision` with `Degradation = PanelUnavailable`, in either mode.
  Never a `Pass`.

Retry and backoff for a transient provider failure happen below this layer, in the existing agent
plumbing; `Degradation` records only what the panel ended up with after those retries were exhausted.

`Degradation` is a non-null enum on `Verdict`
(`None | SingleJudge | PanelUnavailable | ArbiterUnavailable`) so a reader who ignores it cannot
mistake a one-judge verdict for a full-panel one. `ArbiterUnavailable` is the straddle case where
escalation was configured but the arbiter could not be reached: the outcome is `Split`, and the
reason distinguishes "we chose not to escalate" from "we tried and could not".

## 3. Bias controls

This section is the reason the numbers are worth anything. Each control names the failure mode it
defends against and where it is enforced.

### 3.1 Position and order bias

**The failure.** Swapping candidate order, GPT-4 is only 65.0% self-consistent (30% first-position
preference); GPT-3.5 46.2%; Claude-v1 23.8%, i.e. a 75% first-position preference. Renaming the
models barely helps (66.2%) (Zheng et al., 2023, arXiv:2306.05685). Reordering alone flipped a
leaderboard: Vicuna-13B "beat" ChatGPT on 66 of 80 queries under ChatGPT judging
(Wang et al., 2023, arXiv:2305.17926).

**The control.** `JudgeContext.PresentationSeed` is assigned by the harness, never by the judge.

- *Pairwise*: `PairwiseJudgeRunner` runs **both orders** and treats a disagreement between passes as
  **no preference** from that judge — its ballot abstains (§2.8) rather than counting for either
  side. This is Balanced Position Calibration
  (Wang et al., 2023, arXiv:2305.17926).
- *Pointwise with peers*: `JudgeContext.Peers` is shuffled per judge by `PresentationSeed`, so no two
  panel members see the same ordering and a shared positional preference cannot align into consensus.
- The seed is persisted on the `Ballot`, so any verdict is reproducible and a position effect can be
  measured after the fact rather than assumed absent.

**Enforcement, not convention.** `JudgeGauntlet` assigns seeds; an `IJudge` that ignores
`PresentationSeed` is caught by a harness test asserting two seeds produce two different rendered
prompts.

### 3.2 Self-preference bias

**The failure.** GPT-4 awards itself roughly +10% win rate and Claude-v1 roughly +25%
(Zheng et al., 2023, arXiv:2306.05685). It is causally tied to self-recognition: fine-tuning a
model's ability to recognize its own text moves self-preference linearly
(Panickssery et al., 2024, arXiv:2404.13076).

**Why this matters here specifically.** Revobot has this bug today, unmitigated. `JudgeAsync` builds
the judge's agent loop with `run.ModelId` — the model that generated the review under judgement:

```
samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs:3052
    await using var loop = _loopFactory.Create(
        profile, run.ModelId, ThreadId(run, DaemonAgentFactory.JudgeProfileId), ...);
```

Every judge score in the daemon's `judge` artifacts to date was produced by the review's own model
grading itself.

**The controls.**

1. `JudgePanel` **rejects at construction** any judge whose `ModelFamily` equals the candidate's
   generator family, unless `HarnessOptions.AllowSelfFamilyJudge` is explicitly set. A throw, not a
   warning.
2. `Candidate.ModelId` is **never rendered into the judge prompt**. `Candidate.VariantId` is rendered
   only as an opaque label (`"A"` / `"B"`), never a model name — removing the self-recognition cue
   that drives the effect.
3. When the same-family case is deliberately allowed, the fact is stamped on the `Verdict` so
   downstream analysis can segment on it rather than pool it.
4. Panel family-disjointness (§2.12.1) means a self-preferring judge is **detected** — it straddles
   against the other family and the verdict becomes `Split`. Note this is weaker than the
   three-judge case, where such a judge would be outvoted and the verdict would still be correct;
   with two judges we get an alarm, not a correction (§2.12.4).
5. When excluding the generator's family would leave fewer than two judges, the harness runs the
   single remaining judge rather than admitting the generator's own family — admitting it would
   manufacture false consensus, which is worse than a missing judge (§2.12.5).

### 3.3 Verbosity and length bias

**The failure.** A "repetitive list" attack — restating content at greater length with nothing new —
fooled GPT-3.5 and Claude-v1 **91.3%** of the time (GPT-4: 8.7%)
(Zheng et al., 2023, arXiv:2306.05685). Related: LLMBar's adversarial split shows outputs with better
tone and formatting that *violate* the instruction reliably fool judges, while expert humans agree at
94% — the failure is the judge's, not the label's (Zeng et al., 2024, arXiv:2310.07641).

**The controls.**

1. **A length gate before the judge.** `LengthBoundsGate` rejects candidates outside the task type's
   configured band, so extreme cases never reach a model — the cheapest possible defence.
2. **Length is measured and recorded.** `candidate_length` on the experiment record (§6.1) lets a
   score-versus-length regression be run and a length-correlated variant caught empirically instead
   of argued about.
3. **The rubric must not reward length.** `RubricValidator`, run in `tests/LmEval.Tests`, fails any
   rubric whose criterion text contains reward-for-volume language ("comprehensive", "thorough",
   "detailed", "in depth") without a matching anchor that caps it. Crude, but it catches the common
   drafting mistake.
4. **Anchors stated in terms of findings, not prose** — e.g. "every finding cites a file and line
   that resolves" rather than "the review is thorough".

### 3.4 Reference-guided grading

Less a bias control than the largest single accuracy lever: giving the judge an independently
produced reference answer cut GPT-4's math-grading failure rate from 70% to 15%, beating
chain-of-thought's 30% (Zheng et al., 2023, arXiv:2306.05685). `Candidate.Reference` is plumbed
through `JudgeContext.Reference`; the eval runner populates it from the corpus's accepted output
wherever one exists (§5).

### 3.5 What we report, and the ceiling we aim at

Raw percent agreement alone is not acceptable: judges with high percent agreement can still assign
substantially different scores (Thakur et al., 2024, arXiv:2406.12624). The validation report emits
**Krippendorff's alpha** (ordinal; handles the missing data abstentions create; handles per-item
varying rater sets — all three of which we have), alongside raw agreement and Cohen's kappa for the
two-rater case.

**Target the human ceiling, not 100%.** Human-human agreement on MT-Bench is 81–82% with ties
excluded, about 67% with ties included (Zheng et al., 2023, arXiv:2306.05685). A judge reported as
"95% accurate against humans" on a task whose humans agree 81% of the time is measuring something
other than quality. LLMBar reaches 94% expert agreement on a cleaner, more objective task
(Zeng et al., 2024, arXiv:2310.07641) — the ceiling is a property of the rubric, which is an argument
for investing in anchors.

Initial target: **Krippendorff's alpha in the 0.7–0.8 band** against human verdicts, per task type.
Below 0.67 the rubric is treated as not yet fit for gating.

## 4. Revobot migration

**Hard constraint: no behaviour change.** Slice #319 is a refactor. Every existing daemon test in
`tests/CodeReviewDaemon.Sample.Tests` must pass unchanged, persisted artifact shapes stay
byte-compatible, and the judge stays advisory. The bias fixes in §3 become *available* after this
slice but are **not enabled** by it — enabling them is #321/#322 work, gated on measurement.

### 4.1 What Revobot's guardrails actually are

Worth stating plainly, because it reframes the work: **almost all of Revobot's review guardrails are
prose in a prompt file, not code.** The C# has no finding data model at all — the review is a
`string` end to end.

| Guardrail | Where it actually lives | Enforced by |
| --- | --- | --- |
| Review dimensions (architecture, performance, tests, …) | prose in `Prompts/daemon-prompts.yaml:128-135`; the real agent catalog is the **external `code-reviewer` plugin**, not this repo | **the model's own choice.** No dimension type in daemon code. |
| Dimension availability | `GatewaySkillProbe` → `GatewaySkillSupport(HasReviewSkill, ReviewerAgentCount, MarketplaceErrors)`, `Workspace/Sandbox/GatewaySkillProbe.cs:18`, `IsSupported` at `:25` | code — but it only checks `ReviewerAgentCount > 0`. It cannot tell *which* dimensions ran. |
| Sub-agent dispatch | the LLM calls the Agent tool itself | **not C#.** The daemon only waits: `ReviewSubAgentCompletionBarrier.WaitAsync`, `Agents/ReviewSubAgentCompletion.cs:258` |
| Result aggregation | `daemon-prompts.yaml:249-250` — "de-duplicate, drop anything a sub-agent retracted or could not substantiate" | **nothing.** One sentence of prose. The C# only renders an inventory: `ReviewSubAgentTreeSnapshot.ToSafeInventory()`, `Agents/ReviewSubAgentCompletion.cs:147` |
| Adversarial verification of findings | — | **does not exist.** No type takes a finding and challenges it. `ValidateReviewStillCurrentAsync` (`Orchestration/DaemonReviewStageExecutor.cs:2934`) verifies the PR head SHA, not findings. |
| Severity Must/Should/Consider | `daemon-prompts.yaml:213` | prose only; never parsed. No severity enum in C#. |
| Grading | `JudgeAgent` (`Agents/JudgeAgent.cs:19`) + prompt `daemon-prompts.yaml:349-357` | code, but a single judge, a five-line prompt, an unanchored 0–10 scale, and **nothing consumes the score** |
| A/B arms | `ReviewVariant` (`Agents/ReviewVariant.cs:12`), `VariantReviewer` (`Agents/VariantReviewer.cs:24`) | code, and genuinely well-isolated |
| Capability denial | `OperationPolicy.Decide` (`Workspace/OperationPolicy.cs:121`), `ScopedToolFilter.Apply` (`Agents/ScopedToolFilter.cs:20`) | code. Solid. |
| Deterministic caps | input-side only: `MaxExistingCommentsListed = 120` (`DaemonReviewStageExecutor.cs:2198`), `MaxKnowledgeEntries = 24` (`:1564`), `MaxKnowledgeDigestChars` (`:1567`), `UntrustedTranscriptText` caps (`Orchestration/ReviewNotesArtifactBuilder.cs:38`, `:41`) | code. But **no cap, dedupe, or path/line filter on findings.** |
| Retrieval bounding | `KnowledgeDigest.Deduplicate` (`Agents/KnowledgeDigest.cs:258`), `KnowledgeIndex.ParseIndex` | code. Deterministic ranking + caps. |

So the migration is less "move code" and more "give the prose a place to become code". The
code-level pieces that move are the judge, the variant arms, and the artifact shapes.

### 4.2 File by file

**New — `src/LmEval/`** (types per §2)

- `Candidate.cs`, `Gate.cs`, `Rubric.cs`, `Ballot.cs`, `Verdict.cs`, `JudgePanel.cs`,
  `JudgeGauntlet.cs`, `Aggregation/WeightedMedianAggregator.cs`,
  `Judges/RubricJudge.cs` (the `IMultiTurnAgent`-backed default), `HarnessOptions.cs`,
  `Gates/LengthBoundsGate.cs`, `Gates/JsonSchemaGate.cs`, `Gates/RequiredAnchorGate.cs`.
- `src/LmEval/AchieveAi.LmDotnetTools.LmEval.csproj` — references `LmCore`, `LmMultiTurn`;
  `InternalsVisibleTo` → `LmEval.Tests`.
- Register `src/LmEval` and `tests/LmEval.Tests` in `LmDotnetTools.sln`.

**`samples/CodeReviewDaemon.Sample/CodeReviewDaemon.Sample.csproj`**
Add `<ProjectReference Include="..\..\src\LmEval\AchieveAi.LmDotnetTools.LmEval.csproj" />`.

**`samples/CodeReviewDaemon.Sample/Agents/JudgeAgent.cs`** — the main change.
Keep the type name, keep `JudgeArtifactKind = "judge"` (`:23`) and
`JudgeArtifactSchemaVersion = 1` (`:21`), keep `JudgeArtifactPayload(Score, Rationale, VariantId)`
exactly (`:153`). Internally it becomes a thin adapter:

- Build a `Candidate` from `JudgeRequest` (`Agents/JudgeAgent.cs:140`) with
  `TaskType = "code-review"`, `Content = JudgingInput`, `VariantId = request.VariantId`.
- Run a `JudgeGauntlet` configured with **zero gates and a single-judge panel**. That is what
  reproduces today's behaviour exactly.
- Map `Verdict.Score` → the artifact's `score`, the single `Ballot.Reasoning` → `rationale`.
- **Preserve the malformed-response behaviour bit-for-bit for now.** Today `ParseVerdict` returns
  `(0, rawText)` on unparseable output (`:87`, `:110`). Under the harness that is naturally an
  abstention, which would change the persisted score from `0` to absent. Since this slice forbids
  behaviour change, `JudgeAgent` maps `Abstained → score 0, rationale = raw text` **and logs a
  warning naming the abstention**. The `0`-means-two-things defect becomes *visible* in logs without
  changing the artifact. Fixing it properly needs `judge` schema v2, scheduled in §6.3.
- Delete `ParseVerdict` (`:81`) and `UnwrapJson` (`:115`) — the harness's schema-validated parse
  replaces them. Their test coverage moves to `tests/LmEval.Tests` against the harness parser.

**`samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml`**
Add a `judge: v2.0` entry expressing the *same* grading intent as `v1.0` (`:349`) but rendered from a
`Rubric` — anchored criteria, reasoning-before-score. **`v1.0` stays and stays the default** in this
slice. `DaemonAgentFactory.CreateJudgeProfile` (`Agents/DaemonAgentFactory.cs:136`) selects the
version from config, so v2.0 ships dark and is switched on by an experiment in #321.

**`samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs`**
- `JudgeAsync` (`:3022`): no signature change. The `_loopFactory.Create(profile, run.ModelId, …)`
  call at `:3052` **stays as-is** — swapping the judge model is a behaviour change and is #322's job.
  A `// TODO(#322): judge shares the generator's model — self-preference, see P6 §3.2` comment
  records it at the line.
- `RunVariantArmAsync` (`:2993`): unchanged.
- The stage machine is untouched; `ReviewStage` (`Persistence/Models/ReviewRunAxes.cs:8`) keeps its
  five values `Discovered, ContextReady, Reviewed, Judged, Posted`.

**`samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs`**
No new options in this slice. `EnableJudgeAgent` (`:43`), `EnableABVariants` (`:49`),
`VariantModelId` (`:171`), `VariantReasoningEffort` (`:179`), `ReviewReasoningEffort` (`:211`),
`ToolAssistedReasoningEffort` (`:221`) keep their current defaults and meanings. They become inputs
to the routing policy in #322.

**`tests/CodeReviewDaemon.Sample.Tests`**
Existing judge tests must pass **unmodified** — that is the no-behaviour-change proof. Add one
characterization test asserting a malformed judge reply still persists `score = 0` and now also
emits the abstention warning.

### 4.3 What the abstraction cannot express

Called out deliberately rather than papered over.

1. **Model-chosen review dimensions.** Revobot's dimensions are selected at runtime by the reviewing
   model from the external plugin's methodology (`daemon-prompts.yaml:128`); the host only checks
   that *some* reviewer agents exist (`GatewaySkillSupport.ReviewerAgentCount`,
   `Workspace/Sandbox/GatewaySkillProbe.cs:25`). A `Rubric` is a fixed, versioned criterion list.
   These are different things. **Deliberate exclusion.** The harness judges *the review that came
   out*; it does not constrain which dimensions produced it. If dimension coverage later needs
   scoring, it becomes a `RubricCriterion` ("does the review address the dimensions the diff
   implicates?") — a judgement about coverage, not a host-side catalog.

2. **Findings as structured objects.** The review is Markdown prose; severity tags and file:line
   citations exist only as text conventions (`daemon-prompts.yaml:213`). Confirmed: there is no
   `Finding` / `ReviewResult` type anywhere. The closest is the *read-side*
   `ExistingReviewComment(Path, Line, Body, Author, IsActive, PublishedAt, ThreadId)` at
   `Orchestration/IReviewCommentPublisher.cs:76` — and its `Line` is a `string?` that is never
   parsed or validated. A per-finding gate (does this cited line exist? is this a duplicate?) needs a
   parsed finding model that does not exist today. **Extension, scheduled:** `RequiredAnchorGate` in
   #319 does the shallow version (citations present and well-formed); a real `ReviewFinding` parser
   is deferred to #320, where the eval runner needs it anyway to compare two reviews' findings.

3. **Cross-turn / conversational judgement.** Revobot's re-review path reasons about what changed
   since the previous round (prev head SHA, review round, computed in `ComputeRereviewContextAsync`).
   A `Candidate` is a single artifact with no history. **Deliberate exclusion for #319**; the corpus
   in #320 carries whole conversations, so that is the natural home. Recorded as Q5.

4. **The synthesis fold.** `daemon-prompts.yaml:249` folds N sub-agent outputs into one review,
   dropping unsubstantiated findings. That is a *reducer over candidates producing a new candidate*,
   not a judge producing a verdict. `IBallotAggregator` reduces ballots, not content.
   **Extension, deferred:** an `ICandidateReducer` is the obvious sibling interface and is the same
   shape `LmWorkflow` needs for its missing reduce node (§2.11). Not in #319; see Q6.

5. **Capability denial.** `OperationPolicy` (`Workspace/OperationPolicy.cs:121`) gates *what an agent
   may do*, not *whether its output is good*. Superficially `IGate`-shaped, genuinely a different
   concern. **Deliberate exclusion — it stays where it is.** Merging them would put sandbox security
   policy behind an evaluation abstraction, a bad trade in both directions.

## 5. Eval runner (#320)

### 5.1 What the corpus actually is

An eval runner needs recorded (input, output) pairs. The honest position, verified across the repo:
**there is no golden dataset and no input-to-approved-output store anywhere.** The runner's design
has to start from that.

Available sources, in descending order of usefulness:

- **The daemon's `review_run` + `review_artifact` tables** (SQLite; created in
  `samples/CodeReviewDaemon.Sample/Persistence/Migrations/SchemaMigrations.cs`). Every completed
  review is a `(PR diff → review text)` pair already tagged with `VariantId`, `ModelId`,
  `PromptTemplateHash` (`Persistence/Models/ReviewRun.cs:24`, `:30`, `:31`). Artifact kinds present:
  `review`, `review-provisional`, `review-context`, `review-synthesis-request`, `judge`,
  `b-variant-review`, `knowledge`. The payload shape is `ReviewArtifactPayload`
  (`Orchestration/DaemonReviewStageExecutor.cs:3810`), and the input side is
  `ContextArtifactPayload(PrId, BaseSha, HeadSha, Diff, FileManifest, …)` (`:3782`) — an
  input/output pair by construction. **This is the best corpus we have and it is already
  accumulating.**
- **`b-variant-review` artifacts** (`Agents/VariantReviewer.cs:97`) — the collect-only B arm's output
  for the *same* input. Paired by construction, and the single most valuable rows in the corpus,
  because an A/B judgement needs exactly this shape.
- **Merged-PR outcome as a weak label.** `ReviewRun.MergeSha` and `PrLifecycleState`
  (`Persistence/Models/ReviewRunAxes.cs:40`) say whether the PR eventually merged. Weak and
  confounded, but free.
- **Full conversation histories.** `CodeReviewDaemonOptions.ConversationStorePath`
  (`Configuration/CodeReviewDaemonOptions.cs:80`) wires a `FileConversationStore`
  (`src/LmMultiTurn/Persistence/FileConversationStore.cs:13`) at `Program.cs:271-275`, writing
  directory-per-thread JSON (`messages.json`, `metadata.json`, `runs.json`, `run-lifecycle.json`).
  Rich, unlabelled, and includes sub-agent threads. Useful for cost and behaviour replay, not for
  scoring.
- **HTTP-level replay.** `RecordPlaybackMiddleware` (`src/LmTestUtils/MockHttpHandlerBuilder.cs:3105`)
  via `MockHttpHandlerBuilder.WithRecordPlayback(string filePath, bool allowAdditional = false)`
  (`:1848`), over `RecordPlaybackData` / `RecordedInteraction` (`:2767`, `:2776`) with flexible
  matching in `RequestMatcher.MatchesRecordedRequest` (`:2788`). About 45 fixture files exist under
  `tests/TestData/`. These are **single-shot provider fixtures, not task corpora** — they make the
  runner's own tests hermetic; they are not the corpus.
- **Human verdicts.** The strongest available signal is derived, not direct: findings raised in one
  review round and *fixed* in a later one, which `ReviewFeedbackAgent`
  (`Agents/ReviewFeedbackAgent.cs:21`) already distils per developer into
  `KnowledgeBase/developers/<slug>.reviewfeedbacks.md`. A fixed finding is a human implicitly
  agreeing with it.

**Gap, stated plainly:** nothing today records "a human read this agent output and accepted or edited
it" as a first-class row. `RunLedgerEntry.Status` and `RunLifecycleState.Outcome` record *technical*
completion, and `InputAcceptance` / `AcceptedInputEntry`
(`src/LmMultiTurn/Persistence/IRunLedgerStore.cs:13`) are **admission/idempotency tickets despite the
name** — not quality acceptance. Closing that gap is §8's job; until it lands the runner's
human-agreement numbers come from the fixed-finding proxy, with that fact recorded on the run.

### 5.2 Baseline per task type

```csharp
public sealed record EvalBaseline
{
    public required string TaskType { get; init; }
    public required string BaselineId { get; init; }
    public required string RubricId { get; init; }
    public required string RubricVersion { get; init; }
    public required int CorpusSize { get; init; }
    public required double MeanScore { get; init; }
    public required double P10Score { get; init; }
    public required double PassRate { get; init; }
    public required decimal MeanCostUsd { get; init; }
    public required string CorpusSnapshotHash { get; init; }
}
```

A baseline is a **frozen tuple of (corpus snapshot, rubric version, variant config)**. Recorded once,
referenced thereafter, never recomputed silently.

### 5.3 What a run emits

`EvalRun` → one row per run plus one `Verdict` per corpus item, written to the same SQLite store as
the experiment record (§6). A run emits:

- per-item `Verdict` (score, gates, ballots, dispersion, cost);
- aggregate mean / P10 / pass-rate / total cost;
- the **delta against the named baseline**, with a confidence interval;
- the count of `NoDecision` items — a run where the panel could not decide on 30% of items is not a
  clean result even if the remaining 70% look good;
- the **straddle rate** (§2.12.2) and, separately, the share of straddles resolved by arbiter versus
  left as `Split`. This is the primary judge-reliability signal available without human labels, and
  a rising straddle rate on a stable corpus means the rubric is underspecified at its threshold;
- the count of rows with `Degradation != None`, excluded from the aggregates by default (§2.12.6).

### 5.4 Regression detection

A regression is declared when, against the named baseline on the same corpus snapshot and rubric
version:

1. `PassRate` drops by more than the configured margin **and** the drop falls outside a bootstrap 95%
   confidence interval over corpus items; **or**
2. `P10Score` drops materially while `MeanScore` holds — the tail-collapse case a mean hides; **or**
3. the `NoDecision` rate rises materially — the panel has stopped being able to judge, which
   invalidates the comparison rather than passing it.

**Comparisons are refused, not warned about, when `RubricVersion` or `CorpusSnapshotHash` differ.**
A silent cross-version comparison is the most likely way this system produces a confident wrong
number, so it is a hard error.

## 6. Experiment record (#321)

### 6.1 Schema

One row per (candidate, variant, judged) triple, stored in the daemon's existing SQLite database as
**migration V5**. `SchemaMigrations.All` currently runs 1–4
(`samples/CodeReviewDaemon.Sample/Persistence/Migrations/SchemaMigrations.cs:17-20`) with
`LatestVersion` derived at `:12`, so adding V5 is the established, tested path via `MigrationRunner`.

```
experiment_record
  id                    INTEGER PK
  experiment_id         TEXT NOT NULL   -- groups arms of one experiment
  review_run_id         INTEGER NULL    -- FK -> review_run.id; NULL for offline eval items
  eval_run_id           TEXT NULL       -- set for corpus replays
  task_type             TEXT NOT NULL
  variant_id            TEXT NOT NULL   -- 'primary' | 'b' | ...
  model_provider        TEXT NOT NULL
  model_id              TEXT NOT NULL
  reasoning_effort      TEXT NULL       -- NOT available from usage; see §6.2
  prompt_template_hash  TEXT NULL
  rubric_id             TEXT NOT NULL
  rubric_version        TEXT NOT NULL
  gate_outcome          TEXT NOT NULL   -- Pass | Reject | Inconclusive
  gate_reason           TEXT NULL
  judge_score           REAL NULL       -- NULL when NoDecision. NEVER 0-as-missing.
  judge_dispersion      REAL NULL
  ballot_count          INTEGER NOT NULL
  excluded_ballot_count INTEGER NOT NULL
  verdict_outcome       TEXT NOT NULL   -- Pass | Fail | Split | NoDecision
  tie_break_rule        TEXT NULL
  straddled             INTEGER NOT NULL -- 0/1; the judges landed on opposite sides (§2.12.2)
  panel_degradation     TEXT NOT NULL   -- None | SingleJudge | PanelUnavailable | ArbiterUnavailable
  human_verdict         TEXT NULL       -- Accepted | Edited | Rejected | Ambiguous | Ignored
                                        -- NULL = no human signal observed yet
  human_verdict_source  TEXT NOT NULL   -- provenance; see §8.1.2. NOT NULL and no default.
  human_verdict_conf    REAL NULL       -- proxy strength in [0,1]; NULL for Explicit
  human_signal_seen_at  TEXT NULL
  human_edit_distance   INTEGER NULL
  outcome               TEXT NULL       -- terminal business outcome, e.g. Merged | Abandoned
  generator_cost_micros INTEGER NULL    -- USD micro-units, matching UsageRecord
  judge_cost_micros     INTEGER NULL
  cost_provenance       TEXT NOT NULL   -- Unavailable | PublicEstimate | ProviderReported
  input_tokens          INTEGER NULL
  output_tokens         INTEGER NULL
  candidate_length      INTEGER NOT NULL -- for the length-bias regression (§3.3)
  usage_thread_id       TEXT NULL        -- join key into the usage ledger (§6.2)
  created_at            TEXT NOT NULL
```

Indexes on `(experiment_id)`, `(task_type, rubric_version)`, `(review_run_id)`, `(usage_thread_id)`.

Cost is stored in **micro-units (integer)**, matching `UsageRecord.EstimatedPublicCostMicros` /
`ProviderReportedCostMicros`, so no floating-point drift is introduced at the boundary.

Several fields carry deliberate NULL semantics, following the project's not-run-sentinel discipline:
`judge_score` is NULL (never 0) when there is no decision; `human_verdict` NULL means *no human
signal observed yet*, not *rejected*; `outcome` NULL means *not yet terminal*; and `cost_provenance`,
`panel_degradation` and `human_verdict_source` are **non-null enums** precisely so a reader cannot
mistake a NULL for a benign default — a missing cost is not a zero cost, a degraded panel is not a
full one, and a proxy verdict is not a human's stated opinion.

A `CHECK` constraint ties the last pair together: `human_verdict IS NULL` if and only if
`human_verdict_source = 'None'`. It is not possible to record a verdict without saying where it came
from, and the column has **no default**, so a writer that ignores provenance fails to insert rather
than silently claiming `Explicit`.

### 6.2 Joining to usage, and what is actually measured

Cost attribution reuses `src/LmMultiTurn/UsageAccounting/` rather than re-measuring.

What exists today:

- `UsageLedger : IUsageSink` — `src/LmMultiTurn/UsageAccounting/UsageLedger.cs:11`. Constructed as
  `new UsageLedger(threadId, ...)` at `src/LmMultiTurn/MultiTurnAgentLoop.cs:399`.
  `UpsertAttempt(UsageRecord)` (`:66`) merges by `ProviderAttemptId`, `Snapshot(...)` (`:103`),
  `SnapshotRecords()` (`:122`), `SeedFromRecords(...)` (`:135`) for restart rebuild.
- `UsageRecord` — `src/LmCore/Models/UsageRecord.cs:61`. Carries `LogicalCallId`,
  `ProviderAttemptId` (the dedup key), `Revision`, `RootConversationId`, `ParentExecutionId`,
  `ExecutionKind`, `RequestedModel`, `EffectiveModel`, token counts, `EstimatedPublicCostMicros`,
  `ProviderReportedCostMicros`, `Currency`, `Finalized`.
  `enum UsageExecutionKind { Primary, SubAgent, WorkflowController, WorkflowTask, Continuation }`
  (`:21`); `enum CostProvenance { Unavailable, PublicEstimate, ProviderReported }` (`:6`).
- `ConversationUsageProjection` — `src/LmMultiTurn/UsageAccounting/ConversationUsageProjection.cs:19`.
  `SaveAsync` (`:52`), `LoadAsync` (`:210`), `LoadRecordsAsync` (`:221`), `FromMetadata` (`:235`).
  Output type `ConversationUsageAggregate` (`src/LmCore/Models/ConversationUsageAggregate.cs:65`)
  with `PerModel : IReadOnlyList<ModelUsageRow>` (`:22`) and `Fold(...)` (`:107`).

**The join key is `RootConversationId`**, which equals the loop's `threadId` and therefore equals
`PersistedMessage.ThreadId` / `ThreadMetadata.ThreadId`. That is `experiment_record.usage_thread_id`.
The daemon already composes deterministic thread ids per stage via `ThreadId(run, <profileId>)`
(used at `DaemonReviewStageExecutor.cs:3052` for the judge and `:3015` for the B arm), so generator
and judge cost land on **different thread ids under the same review run** — which is exactly what
lets `generator_cost_micros` and `judge_cost_micros` be separated cleanly.

`JudgeGauntlet` takes an optional `IUsageSink` (§2.10) so judge cost is captured by the same
machinery as generator cost and the two are comparable by construction.

Three facts the implementer must know, because they change what §7 can be built on:

1. **Usage is not a SQL table.** It is persisted into `ThreadMetadata.Properties` as JSON strings
   under the keys `"usage.aggregate"` and `"usage.records"`
   (`ConversationUsageProjection.cs:22`, `:25`), written through
   `IConversationStore.UpdateMetadataAsync`. Reading it for analysis means reading the conversation
   store, not joining a table — which is one more reason §6.1 denormalizes cost into
   `experiment_record`.
2. **Dollar estimation is dead code in practice.** `IPricingResolver`
   (`src/LmCore/Models/ModelPricing.cs:46`) has exactly one implementation,
   `PricingConfigResolver` (`src/LmConfig/Pricing/PricingConfigResolver.cs:12`), and **nothing in the
   repo ever constructs it** — `MultiTurnAgentLoop`'s `IPricingResolver? pricingResolver = null`
   parameter is never supplied. So `EstimatedPublicCostMicros` is always null today and only
   `ProviderReportedCostMicros` is populated, from `UsageMessage.Usage.TotalCost` via
   `UsageRecordMapper.ToMicros` (`src/LmMultiTurn/UsageAccounting/UsageRecordMapper.cs:72`).
   **Slice #321 must wire `PricingConfigResolver` from `src/LmConfig/docs/models.json`** or every
   experiment row for a provider that does not self-report cost carries
   `cost_provenance = Unavailable`, and #322 has nothing to optimize against.
3. **There is no per-effort attribution.** `UsageRecord` has no reasoning-effort field, and neither
   does `ModelUsageRow`. Effort exists at `SubAgentCharacteristics.Effort`
   (`src/LmMultiTurn/SubAgents/SubAgentCharacteristics.cs:10`) and in the daemon's options, but it is
   never propagated onto a usage record. So `experiment_record.reasoning_effort` must be **stamped by
   the harness's caller from the configured value**, not derived from usage. Deriving it later is
   impossible; not stamping it makes the effort axis unmeasurable, which is half of #322.

**Deliberately denormalized.** Cost is *copied* into `experiment_record` at write time rather than
joined at read time. The ledger is a live, revision-watermarked projection
(`RevisionWatermark`, `src/LmMultiTurn/UsageAccounting/RevisionWatermark.cs:14`); an experiment record
must be immutable to be citable. A later ledger correction must not silently restate a past
experiment's conclusion.

### 6.3 `judge` artifact schema v2

Once the experiment record exists, `JudgeArtifactPayload`
(`samples/CodeReviewDaemon.Sample/Agents/JudgeAgent.cs:153`) gains
`JudgeArtifactSchemaVersion = 2` with additive-optional `Dispersion`, `BallotCount` and a **nullable**
`Score`, retiring the `0`-means-parse-failure conflation from §4.2. Readers must handle both
versions; `ReviewArtifact.ArtifactSchemaVersion`
(`samples/CodeReviewDaemon.Sample/Persistence/Models/ReviewArtifact.cs:17`) exists for exactly this.

## 7. Routing cascade (#322)

### 7.1 The mechanism

Today routing is six hand-tuned constants in `CodeReviewDaemonOptions.cs` (`:43`, `:49`, `:171`,
`:179`, `:211`, `:221`). The cascade replaces the *choice* of those values with a fit over
`experiment_record`, keeping the constants as the fallback when data is insufficient.

The shape follows FrugalGPT: a cascade in ascending cost order with a scoring function and a learned
per-stage threshold — stop if the score clears the threshold, else escalate. FrugalGPT matched GPT-4
accuracy at up to 98% cost reduction, or +4% accuracy at equal cost
(Chen, Zaharia & Zou, 2023, arXiv:2305.05176). RouteLLM's complementary pre-generation router,
trained on preference data with a cost-quality parameter, reported >85% cost reduction on MT-Bench
while retaining ~95% of GPT-4 performance (Ong et al., 2024, arXiv:2406.18665).

```csharp
public sealed record CascadeStage(string StageId, string ModelId, string? ReasoningEffort,
    double EscalateBelowScore);

public interface IRoutingPolicy
{
    RoutingDecision Decide(string taskType, IReadOnlyDictionary<string, string> signals);
}
```

**Hard constraint on model selection (from §2.12.5).** The cascade must never select a generator
whose `ModelFamily` is one of the two configured judge families. With only two families reachable,
a generator sharing a judge family forces the panel down to a single judge on every run, which
silently degrades every verdict the cascade then optimizes against. `IRoutingPolicy` validates this
at configuration time and refuses to start with a colliding cascade, rather than discovering it per
candidate.

`EscalateBelowScore` per stage is **fitted**, per task type, from `experiment_record`: choose the
threshold minimizing expected cost subject to a pass-rate floor relative to the always-expensive
baseline. When a task type has fewer than N records the policy returns the configured constant and
**says so** in `RoutingDecision.Reason`, so an unfitted route is never mistaken for a fitted one.

The effort axis depends entirely on §6.2(3) — if `reasoning_effort` is not stamped at write time,
half of this fit has no input.

### 7.2 The guardrail: a cheap verifier rubber-stamps

This is the failure mode that would quietly destroy the whole engine, so it is enforced in code, not
documented as advice.

**The evidence.** With an imperfect verifier, resampling *cannot* reduce the false-positive rate — it
imposes a hard accuracy ceiling regardless of compute spent, and single-sample accuracy correlates
strongly with false-positive rate on HumanEval/MBPP (Stroebl, Kapoor & Narayanan, 2024,
arXiv:2411.17501). The generation-verification gap is real and scales with pretraining compute
(Song et al., 2024, arXiv:2412.02674): verifier capability is not free.

**The rules.**

1. **`JudgePanel` refuses to construct a panel whose members are all cheaper-tier than the generator
   for a gating decision.** `HarnessOptions.RequireJudgeTierAtLeastGenerator` defaults to `true`.
   Overriding it stamps the `Verdict` with `judge_tier_below_generator`, and the eval runner reports
   those rows separately rather than pooling them.
2. **The counterweight is weighting, not permission.** Weak verifiers are not useless in aggregate:
   weak-supervision-*weighted* ensembles of <=70B judges reach o3-mini-level selection accuracy
   (87.7% avg) with a Llama-3.3-70B generator, and weighted significantly beats unweighted
   (Saad-Falcon et al., 2025, arXiv:2506.18203). So a cheap panel is permitted **only** as the
   full family-disjoint, reliability-weighted two-judge panel with an arbiter configured for
   straddles (§2.9, §2.12) — never a single cheap judge, and never a cheap panel running degraded.
   A `Degradation` of `SingleJudge` disqualifies the verdict from gating outright.
3. **A routing change must clear an eval gate.** No cascade threshold ships without a #320 run on the
   named baseline showing pass-rate within the configured margin. The routing policy is data-driven
   in both directions: the data proposes it, and the data has to ratify it.
4. **Cheap-gathers / expensive-judges is the asymmetry.** Gathering (read files, list changes, run a
   deterministic gate) is verifiable work whose output is checkable — route it cheap. Judgement is
   the step with no cheaper checker downstream — route it expensive. The cascade encodes that
   asymmetry rather than a global "use the cheap model more".

## 8. Human-edit feedback (#323)

### 8.1 Capturing the signal: proxy now, explicit control later

**Decision (Q8, now closed): do both.** Start harvesting a corpus immediately from GitHub signals the
daemon already sees, and add an explicit reviewer disposition control in a later slice. Waiting for
the explicit control would mean the first fitted judge weights arrive months after the harness does;
harvesting a proxy without ever adding the explicit control would mean calibrating quality against a
signal that never gets validated. Both, in that order, with the two kept rigorously separate.

#### 8.1.1 The proxy signals

All four are already observable in the daemon today — this is extraction work, not new plumbing.

| # | Signal | Where it comes from |
| --- | --- | --- |
| S1 | A bot comment thread is **resolved** | `ExistingReviewComment.IsActive` (`samples/CodeReviewDaemon.Sample/Orchestration/IReviewCommentPublisher.cs:76`); bot authorship via the `[{{ bot_name }}]` marker (`Prompts/daemon-prompts.yaml:265`), detected at `Orchestration/DaemonReviewStageExecutor.cs:2304` |
| S2 | The finding's **cited lines changed** in a later commit | diff between the review's `HeadSha` and a later head, already computed for re-review context (prev head SHA / review round) |
| S3 | A bot comment was **deleted** | present in round N's comment listing, absent in round N+1 under the same marker |
| S4 | The thread was still **open at PR close/merge** | `ReviewRun.MergeSha`, `PrLifecycleState` (`Persistence/Models/ReviewRunAxes.cs:40`) |

S2 requires resolving a finding to a file and line range, which is the shallow `ReviewFinding` parser
from §4.3(2) — already scheduled in slice #320. That is the dependency that puts proxy harvest after
the eval runner.

#### 8.1.2 Mapping signals to `human_verdict`

The mapping is deliberately conservative: **only the combination of resolution *and* a code change
earns `Accepted`.** Everything weaker keeps its ambiguity in the data.

| Signals | `human_verdict` | `human_verdict_source` | `conf` |
| --- | --- | --- | --- |
| S1 and S2 | `Accepted` | `ProxyResolvedAndChanged` | 0.8 |
| S1, not S2 | `Ambiguous` | `ProxyResolvedUnchanged` | 0.3 |
| S3 | `Rejected` | `ProxyDeleted` | 0.6 |
| S4 | `Ignored` | `ProxyIgnored` | 0.5 |
| a reviewer states it | `Accepted`/`Edited`/`Rejected` | `Explicit` | NULL |
| nothing observed | NULL | `None` | NULL |

Note what the source column names: **the evidence, not the conclusion.**
`ProxyResolvedUnchanged` says "the thread was resolved and the code did not change" — a fact. It does
not say "the reviewer agreed". A later reader who disagrees with our inference can re-derive a
different verdict from the same recorded evidence, which would be impossible if we had stored only
`Ambiguous`.

#### 8.1.3 Keeping the proxy's noise visible

A resolved thread genuinely means one of at least three things: *fixed*, *won't fix*, or *tidying up
a stale thread before merge*. The schema keeps that visible rather than flattening it, in three ways:

1. **`Ambiguous` is a real value of `human_verdict`.** The resolved-but-unchanged case is not coerced
   into `Accepted` to make the column tidier. It is the single largest noise source and it is
   labelled as such.
2. **`Ignored` is distinct from `Rejected`.** A thread nobody ever touched is not a reviewer
   disagreeing; conflating them would systematically understate agreement.
3. **`human_verdict_conf` carries the strength**, so a downstream fit can weight an
   `Accepted`-at-0.8 below an `Explicit` accept rather than treating them as the same observation.

#### 8.1.4 How a proxy verdict is never silently mixed with an explicit one

Three enforcement points, none of them conventional:

1. **Schema.** `human_verdict_source` is `NOT NULL` with **no default**, plus the `CHECK` in §6.1
   tying it to `human_verdict`. A writer cannot omit provenance.
2. **Default read path.** Judge calibration (§8.3) and every §5 aggregate read
   `human_verdict_source = 'Explicit'` **only**. Admitting proxy rows requires an explicit
   `IncludeProxyVerdicts` flag, and that flag is **stamped onto the resulting artifact** — a
   `JudgeReliability` row records the source set it was fitted from, so a weight derived from proxies
   is itself labelled as such and cannot later be mistaken for a human-validated one.
3. **Refusal to pool.** The eval runner refuses to compare or aggregate across differing
   `human_verdict_source` sets, exactly as it refuses across `RubricVersion` and
   `CorpusSnapshotHash` (§5.4). It is a hard error, not a warning, for the same reason: a silent
   pooling is the most plausible route to a confident wrong number.

Once the explicit control ships, the proxy becomes independently checkable: on rows carrying **both**
an explicit verdict and a proxy-derived one, the proxy's agreement with the human is measurable. That
number is the proxy's own validation, and until it exists the proxy's confidence values above are
declared estimates, not measurements.

### 8.2 Turning an edit into durable context

The pipeline exists and is well-built; #323 adds a source, not a mechanism.

`KnowledgeAgent` + `KnowledgeExtractionCommitter` write curated entries to
`KnowledgeBase/<scope>/<slug>.md` with `## UPDATES:` de-duplication and daemon-injected frontmatter.
`KnowledgeIndex` maintains `_index.jsonl` / `_toc.md`; `KnowledgeDigest` does host-side,
relevance-ranked retrieval against the PR's changed paths — `Deduplicate` at
`Agents/KnowledgeDigest.cs:258`, ranking at `:1683` — rendering each surviving entry with its exact
absolute path, under caps `MaxKnowledgeEntries = 24`
(`Orchestration/DaemonReviewStageExecutor.cs:1564`) and `MaxKnowledgeDigestChars`(`:1567`). PR #256
established that retrieval must be host-side and by exact path, because Grep-based retrieval silently
no-ops.

#323 adds an **edit-derived extraction pass**: where a human corrected the agent, the correction —
not the original finding — becomes the candidate lesson. Two rails, both inherited:

- **No path component comes from the model.** `ReviewFeedbackAgent`'s design rule
  (`Agents/ReviewFeedbackAgent.cs:14-18`) — the output path is derived host-side from the
  provider-reported identity — applies unchanged to edit-derived entries.
- **A knowledge entry is prior output, never a mandate.** `daemon-prompts.yaml:220` already states
  that a KB lesson can inform a finding but never authorise a delivery or write action. Edit-derived
  entries have better provenance and get no more authority.

### 8.3 Closing the measurement loop

Human verdicts are what calibrate the judges. `JudgeReliability` (§2.9) is fitted per
`(JudgeId, TaskType, RubricVersion)` from agreement with `human_verdict`, and §3.5's Krippendorff
alpha is computed against the same column. Until enough human verdicts accumulate, every judge weight
stays 1.0 and the harness reports its alpha as *not yet estimable* rather than computing a number
from four data points.

Both readers obey §8.1.4: they consume `human_verdict_source = 'Explicit'` by default, and any fit
that admits proxy rows records that fact on the `JudgeReliability` row it produces. A weight fitted
from proxies is usable — it is better than the 1.0 default — but it is never presentable as
human-validated.

There is also a second, label-free calibration signal available immediately: the **straddle rate**
(§2.12.3). Two disjoint families disagreeing needs no human at all, and it is the only reliability
estimate the harness has on day one, before any verdict of either provenance has accrued.

## 9. Delivery slices

Each slice is one PR. Every slice states what ships, what it depends on, and how it is verified.

### Slice 1 — #319 · The harness

**Ships:** `src/LmEval` with `Candidate`, `IGate`/`GateDecision`, `Rubric`/`RubricCriterion`,
`IJudge`/`Ballot`, `Verdict`, `JudgePanel`, `IBallotAggregator` + `WeightedMedianAggregator`,
`JudgeGauntlet`, `RubricJudge`, three starter gates; `tests/LmEval.Tests`; both in the solution.
Revobot's `JudgeAgent` re-seated on it per §4.2, plus the daemon's new project reference.

**Depends on:** nothing.

**Verified by:** (a) every existing test in `tests/CodeReviewDaemon.Sample.Tests` passing
**unmodified** — the no-behaviour-change proof; (b) harness unit tests covering at minimum: the
position seed actually changes the rendered prompt; a same-family panel throws; a panel including
the generator's family throws; an abstention is excluded rather than counted as zero; a first-`Reject`
gate short-circuits with **no model call** (asserted against a call-counting fake); `NoDecision` when
fewer than `MinCountedBallots` survive.

The two-panel logic of §2.12 needs its own cases: same-side scores decide without invoking the
arbiter (asserted against a call-counting arbiter fake); opposite-side scores escalate exactly once;
an unavailable arbiter yields `Split` with `Degradation = ArbiterUnavailable`, distinguishable from
the not-configured case; advisory mode with one reachable judge yields a verdict marked
`SingleJudge` with a null `Dispersion`, while **gating mode on the same input yields `NoDecision`**;
excluding the generator's family down to one judge degrades rather than admitting that family.

Every bias control in §3 carries a mutation proof — the assertion must break when the control is
removed.

### Slice 2 — #320 · Eval runner

**Ships:** corpus reader over `review_run` / `review_artifact` (pairing `ContextArtifactPayload` input
with `ReviewArtifactPayload` output, and `b-variant-review` as the paired B arm), `EvalBaseline`, the
runner, the delta-and-regression report, and the hard refusal to compare across `RubricVersion` or
`CorpusSnapshotHash`. The shallow `ReviewFinding` parser noted in §4.3(2).

**Depends on:** Slice 1.

**Verified by:** a replay over a fixture corpus reproducing a known baseline deterministically (using
`RecordPlaybackMiddleware` so no provider is called); a seeded regression from a deliberately
degraded variant detected; a cross-rubric-version comparison refused with a clear error; a
tail-collapse case (mean flat, P10 down) detected.

### Slice 3 — #321 · Experiment record

**Ships:** SQLite migration V5 per §6.1; write path from `JudgeGauntlet` and the eval runner;
**wiring `PricingConfigResolver` so cost is actually estimated** (§6.2(2)); effort stamped at write
time (§6.2(3)); `judge` artifact schema v2 (§6.3); `JudgeReliability` fitting.

**Depends on:** Slices 1 and 2.

**Verified by:** migration up-and-idempotent tests against the existing `MigrationRunner`; a
round-trip test proving `judge_score` is NULL — not 0 — for a `NoDecision`; a cost-attribution test
proving judge and generator cost land in separate columns keyed on their distinct thread ids and sum
to the ledger's total; a test that `cost_provenance` is `PublicEstimate` once the pricing resolver is
wired (this is the regression guard against it silently reverting to dead code); a v1-artifact reader
still parsing after v2 ships.

### Slice 4 — #322 · Routing cascade

**Ships:** `IRoutingPolicy`, `CascadeStage`, the threshold fit over `experiment_record`, the
insufficient-data fallback that names itself, and the §7.2 guardrails
(`RequireJudgeTierAtLeastGenerator`, the weighted-panel exemption, the eval gate on threshold
changes).

**Depends on:** Slice 3 — it needs accumulated records, and specifically needs the cost and effort
columns to be populated rather than null.

**Verified by:** a fit over a synthetic record set producing the known-optimal threshold; a
below-minimum-data case returning the constant **and** saying so in `RoutingDecision.Reason`; a
single-cheap-judge gating panel refused; a cheap **weighted family-disjoint** panel permitted; an
end-to-end check that a threshold change without a passing #320 run is rejected.

### Slice 5 — #323a · Proxy harvest

**Ships:** extraction of signals S1–S4 (§8.1.1) into `human_verdict` / `human_verdict_source` /
`human_verdict_conf`; the `CHECK` constraint and no-default provenance column; the
`Explicit`-only default read path with the `IncludeProxyVerdicts` flag stamped onto any
`JudgeReliability` it produces; the eval runner's refusal to pool across source sets; and the
edit-derived knowledge extraction pass of §8.2.

**Depends on:** Slice 3 for the schema, **and Slice 2** for the shallow `ReviewFinding` parser that
signal S2 needs to resolve a finding to a line range.

**Verified by:** each of the four signal combinations in §8.1.2 mapping to the stated verdict and
source; a resolved-but-unchanged thread landing as `Ambiguous`, **not** `Accepted` (the flattening
this slice exists to prevent); `Ignored` distinct from `Rejected`; an insert omitting
`human_verdict_source` failing rather than defaulting; a calibration fit over mixed sources refusing
to pool unless the flag is set, and stamping the flag on its output when it is; an edited comment
producing exactly one KB entry with a host-derived path, and a model-supplied path rejected.

### Slice 6 — #323b · Explicit reviewer disposition

**Ships:** the reviewer-facing disposition control, writing `human_verdict_source = 'Explicit'` with
`human_edit_distance`; and the proxy-validation report — on rows carrying both an explicit and a
proxy verdict, the proxy's measured agreement with the human (§8.1.4), which converts the confidence
values in §8.1.2 from estimates into measurements.

**Depends on:** Slice 5.

**Verified by:** an explicit verdict overriding a previously recorded proxy on the same row while the
proxy's evidence remains recoverable for the agreement report; the agreement report computed only
over rows holding both; alpha and reliability reported as *not estimable* below the minimum sample
count rather than computed from a handful of rows.

## 10. Open questions

Q1, Q2 and Q8 are closed; their answers are worked through in §2.12 and §8.1 respectively. The
numbering of the remaining questions is left unchanged so existing references stay valid.

- **Q3 — Logprobs.** G-Eval's probability-weighted scoring (Liu et al., 2023, arXiv:2303.16634)
  materially improves resolution but needs token logprobs, which `IMultiTurnAgent` does not surface
  and several providers do not offer. Worth a provider-capability probe, or do we accept integer
  scores with a wider panel instead?
- **Q4 — Corpus size and staleness.** How many `review_run` rows exist today, and how many are recent
  enough (same prompt version, same model generation) to be a valid baseline? A baseline over a
  corpus generated by a retired prompt measures the retired prompt. The prompt axis is already
  recorded as `ReviewRun.PromptTemplateHash`, so this is answerable with a query — it just has not
  been run.
- **Q5 — Multi-turn candidates.** Revobot's re-review reasons across rounds; `Candidate` is
  single-shot (§4.3(3)). Does `Candidate` grow a conversation-shaped variant in #320, or does the
  corpus flatten a conversation into an input/output pair and accept the loss?
- **Q6 — `ICandidateReducer`.** The synthesis fold (§4.3(4)) and `LmWorkflow`'s missing reduce node
  (§2.11) are the same shape. Build that seam in `LmEval` now so both consumers get it, or put it in
  `LmWorkflow` as a proper reduce node with `LmEval` supplying the reducer?
- **Q7 — Whether the judge ever gates.** §1 keeps verdicts advisory. The margin thesis eventually
  wants an agent that declines to ship its own bad output. What alpha, on what corpus size, is the
  bar for promoting a verdict from advisory to gating?
