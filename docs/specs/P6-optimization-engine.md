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
- **Not a human-labelling product.** No annotation tool, no labelling queue, no rater management. The
  one exception is deliberate and small: slice #323c adds a *single* reviewer disposition affordance
  (§8.1.3), because without one human verdict the proxy can never be validated. That is an
  affordance, not a product.
- **No new provider abstractions.** Judges are driven through the existing `IMultiTurnAgent` seam.

### Deliberately unspecified in these slices

This spec specifies **slice #319 to implementable depth** and fixes the decisions that are expensive
to reverse. It deliberately does **not** define API surface for decisions nobody has taken yet —
specifying these wrongly is worse than leaving them open:

| Cut | Why | Reopened by |
| --- | --- | --- |
| `GatingMode` and every gating-only rule | Verdicts are advisory in #319–#323 (Q7 is open). A public contract that encodes gating behaviour commits us to a reliability threshold and corpus size nobody has agreed | Q7 |
| Pairwise judging (`JudgeContext.Peers`, `PairwiseJudgeRunner`, presentation seeds) | No slice ships a ranking consumer; every runner and record here is pointwise | a ranking story, if one is ever raised |
| `IRoutingPolicy` / `CascadeStage` C# | §7's *decisions* are what matter now; the interface shape depends on the cascade executor, which is three slices away | #322 |
| Model *tier* as a typed concept | Only the cascade needs an ordering over model strength | #322 |
| `AllowSameFamilyPanel` (permitting a two-same-family panel) | Only two families are reachable, so the flag's only legal use is the false-consensus failure §2.12.2, §2.12.4 and §2.12.5 each argue against; representing the panel it permits was the more expensive of the two closures | a third reachable family, which would make it a different question |
| Per-criterion and confidence-based ballot analytics | `experiment_ballot` is a declared lossy tally summary (§6.1); criterion detail and confidence are not persisted, and no claim here reads them back | a consumer that needs them, which buys the schema change |

Where those appear below they appear as decisions and constraints, not as types.

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
`IMultiTurnAgent`.

**Layering rule: `LmEval` owns no persistence and no orchestration.** `JudgeGauntlet.RunAsync`
returns a complete `Verdict` and knows nothing about SQLite, migrations, the daemon's schema, or
model routing. Everything that stores or sequences is the **host's** job:

| Concern | Owner |
| --- | --- |
| Deciding a verdict | `LmEval` |
| Persisting a verdict, ballots, human observations | the daemon (§6) — its database, its migrations |
| Measuring and attributing cost | the host, around the judge invocation (§6.2) |
| Sequencing generate → evaluate → escalate | the host (§7) |

This is why the harness contract carries no cost type and takes no usage sink. `IUsageSink` is
write-only and `IMultiTurnAgent` offers no way to attach one after construction, so a gauntlet that
promised per-ballot cost could not honour it. The host already owns the agent and its thread id,
which is exactly the join key §6.2 uses, so it is the layer that *can* measure cost — and the layer
that should.

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
    /// <summary>Model family of whatever produced Content. Required for generator-family exclusion
    /// (§3.2); when null, exclusion cannot be applied and the verdict records that fact. Resolved
    /// by the host — LmEval does not own a model taxonomy.</summary>
    public string? GeneratorFamily { get; init; }
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
- **Pointwise only.** Pairwise comparison tracks human preference better
  (Liu et al., 2024, arXiv:2403.16950), but pairwise preferences flip ~35% of the time under
  distractor features versus ~9% for absolute scores (Tripathi et al., 2025, arXiv:2504.14716), and
  no consumer in these slices ranks candidates — the eval runner and the experiment record both
  store pointwise scores. Pairwise is out of scope (§1).
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
    /// <summary>The rubric-weighted average of CriterionScores, on the rubric's own scale:
    /// <c>sum(c.Weight * CriterionScores[c.CriterionId]) / sum(c.Weight)</c> over
    /// <see cref="Rubric.Criteria"/>. Because it is normalised by total weight it stays within
    /// [MinScore, MaxScore] and is directly comparable to Rubric.PassThreshold. A criterion the
    /// judge did not score makes the ballot invalid — it is a schema violation, not a zero.</summary>
    public required double WeightedScore { get; init; }
    public required string Reasoning { get; init; }
    /// <summary>Self-reported confidence in [0,1]. Below the abstain floor the ballot is recorded
    /// but excluded from the tally (§2.8).</summary>
    public required double Confidence { get; init; }
    /// <summary>True when the judge declined to score. An abstention is DISTINCT from a zero.</summary>
    public required bool Abstained { get; init; }
    public string? AbstainReason { get; init; }
    /// <summary>The reliability weight aggregation applied (§2.9), recorded so a past verdict
    /// stays auditable after the weights are refitted. <b>Null as a judge returns it</b> — a judge
    /// cannot know its own weight, and the snapshot does not exist until <c>Aggregate</c> runs.
    /// Aggregation records each counted ballot as <c>ballot with { AppliedWeight = w }</c>, so the
    /// invariant is: non-null on every ballot in <c>Verdict.Ballots</c>, null on every one in
    /// <c>ExcludedBallots</c>. That is exactly what <c>experiment_ballot.applied_weight</c>
    /// persists (§6.1).</summary>
    public double? AppliedWeight { get; init; }
}

public interface IJudge
{
    string JudgeId { get; }
    string ModelId { get; }
    string ModelFamily { get; }
    Task<Ballot> JudgeAsync(Candidate candidate, Rubric rubric, JudgeContext context,
        CancellationToken cancellationToken);
}

/// <summary>Per-invocation input the harness supplies, not the judge implementation.</summary>
public sealed record JudgeContext
{
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
    /// <summary>Aggregated score on the rubric's scale. Null whenever there are **no counted
    /// ballots** — a gate rejection (which short-circuits before any judge runs) and a NoDecision
    /// alike. A gate-rejected candidate has Outcome = Fail with Score = null; the two carry
    /// different information and neither implies a numeric score.</summary>
    public double? Score { get; init; }
    public required IReadOnlyList<GateDecision> GateDecisions { get; init; }
    public required IReadOnlyList<Ballot> Ballots { get; init; }
    /// <summary>Ballots cast but excluded, each with why. Never silently dropped.</summary>
    public required IReadOnlyList<ExcludedBallot> ExcludedBallots { get; init; }
    /// <summary>Disagreement among counted ballots. High dispersion is a
    /// review-this-by-hand signal. <b>Null</b> whenever dispersion is undefined rather than zero:
    /// a single counted ballot (§2.12.6), a gate short-circuit with no ballots, or a NoDecision.
    /// Null is not 0.0 — a lone judge is not a panel in perfect agreement.</summary>
    public double? Dispersion { get; init; }
    public required string RubricId { get; init; }
    public required string RubricVersion { get; init; }
    public required string TieBreakRule { get; init; }
    public required PanelDegradation Degradation { get; init; }
    /// <summary>Names the unreachable family when Degradation is not None. Stable, non-sensitive
    /// text only — same rail as GateDecision.Reason (§2.11).</summary>
    public string? DegradationReason { get; init; }
}

public sealed record ExcludedBallot(Ballot Ballot, string ExclusionReason);

/// <summary>Everything the reduction step needs beyond the ballots themselves. Passed in rather
/// than captured at construction so a verdict records the exact weights it was computed from.</summary>
public sealed record AggregationContext
{
    public required HarnessOptions Options { get; init; }
    /// <summary>Reliability snapshot keyed by JudgeId for this (TaskType, RubricVersion) (§2.9).
    /// A judge absent from the map weighs 1.0.</summary>
    public required IReadOnlyDictionary<string, double> Reliability { get; init; }
    /// <summary>Judges that faulted rather than returning a ballot, and why — this is how
    /// degradation is classified (§2.12.6).</summary>
    public required IReadOnlyList<JudgeFault> Faults { get; init; }
}

public sealed record JudgeFault(string JudgeId, string ModelFamily, string Reason);

public interface IBallotAggregator
{
    string RuleId { get; }
    Verdict Aggregate(Candidate candidate, Rubric rubric,
        IReadOnlyList<GateDecision> gates, IReadOnlyList<Ballot> ballots,
        AggregationContext context);
}
```

**Default aggregator: `WeightedMeanAggregator`.**

1. Drop every ballot with `Abstained == true` or `Confidence < HarnessOptions.AbstainFloor`
   (default `0.34`), recording each in `ExcludedBallots`.
2. If **no** ballot survives → `NoDecision`. **Not a fail.** An unmeasured candidate and a bad
   candidate are different facts. A single surviving ballot *is* counted, and the verdict is marked
   `Degradation = SingleJudge` (§2.12.6) with a null `Dispersion` — verdicts are advisory in these
   slices, so one judge's read is worth recording provided it is labelled as one judge's read.
3. With two counted ballots, apply the straddle test of §2.12.2 — same side of `PassThreshold`
   decides directly, opposite sides runs the tie-break of §2.12.3.
4. `Score` is the reliability-**weighted mean** of the counted ballots — with weights `w_i` from
   §2.9, `Score = sum(w_i * s_i) / sum(w_i)`. With one counted ballot it is that ballot's score. On
   an arbiter-resolved straddle it is the arbiter's score, not a blend.

   Each counted ballot is re-recorded as `ballot with { AppliedWeight = w_i }` before it is
   placed in `Verdict.Ballots`. The aggregator is the **only** component that writes that field,
   because it is the only one holding the snapshot; an `ExcludedBallot` keeps it null.

   *No median is defined anywhere in this spec, and the aggregator is named for what it computes.*
   A median only earns its robustness at three or more ballots; §2.12 fixes the panel at two, where
   it is identical to a mean. The straddle test, not a robust statistic, is what protects against a
   single bad judge.

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
/// <summary>The complete option surface. Validated once at construction; see §2.12.1 for the
/// configuration rules that throw.</summary>
public sealed record HarnessOptions
{
    /// <summary>Ballots below this self-reported confidence are excluded from the tally (§2.8).</summary>
    public double AbstainFloor { get; init; } = 0.34;
    /// <summary>Dispersion above which the verdict is flagged for human review (§2.8). Null
    /// disables the alarm.</summary>
    public double? DispersionAlarm { get; init; } = null;
    /// <summary>Optional stronger model that decides a straddle (§2.12.3). Null means straddles
    /// terminate as Split.</summary>
    public IJudge? ArbiterJudge { get; init; }
}

public sealed class JudgeGauntlet
{
    public JudgeGauntlet(IReadOnlyList<IGate> gates, IReadOnlyList<IJudge> judges,
        IBallotAggregator aggregator, HarnessOptions options,
        ILogger<JudgeGauntlet>? logger = null);

    public Task<Verdict> RunAsync(Candidate candidate, Rubric rubric,
        IReadOnlyDictionary<string, double> reliability,
        CancellationToken cancellationToken);
}
```

`RunAsync`:

1. Run gates in order; short-circuit on the first `Reject`, emitting `Outcome = Fail` with no
   ballots and a null `Score`. No judge runs, so this path costs nothing.
2. `JudgePanel.Compose(judges, candidate, options)` (§2.12.1) — a **pure** eligibility filter over
   model families. It never probes a provider.
3. Fan the eligible judges out concurrently. A judge that faults becomes a `JudgeFault` rather than
   propagating; a judge that returns gives a `Ballot`.
4. `aggregator.Aggregate(...)` with an `AggregationContext` carrying the options, the reliability
   snapshot, and the faults.
5. **If that verdict is an unresolved `Split` and the arbiter condition of §2.12.3 rule 1 holds**,
   await one `JudgeAsync` on the arbiter and call `aggregator.Aggregate(...)` a **second** time — with
   the arbiter's ballot appended to `ballots`, or, if the arbiter faulted, with a `JudgeFault` for it
   appended to `context.Faults`. The second verdict is the emitted one. **When that condition does not
   hold, no arbiter call is made and the second reduction does not run** — the step-4 `Split` is the
   emitted verdict. The condition is stated once, in §2.12.3 rule 1; this step executes it and does not
   restate it, for the same reason step 2 defers the eligibility filter to §2.12.1.

**The escalation boundary is `RunAsync`, not the aggregator.** `Aggregate` is synchronous by design:
it is a pure reduction over ballots and cannot await an `IJudge`, so the one asynchronous call
§2.12.3 requires is made by the gauntlet, exactly as gate execution and panel fan-out are. The
aggregator is therefore invoked **at most twice** per candidate and is pure both times. It tells the
passes apart from data it already holds — `context.Options.ArbiterJudge` names the arbiter, so a
ballot from that `JudgeId` is the arbiter's deciding vote (§2.12.3 rule 1), a `JudgeFault` for that
`JudgeId` is `ArbiterUnavailable`, and a straddle with neither present is `"split:unresolved"`. That
is the whole of the arbiter's result-or-fault path: no new type, and no I/O behind a pure
interface.

**Degradation is classified after fan-out, from what actually came back** — not from a health probe
before it. That ordering is forced: `IJudge` exposes no reachability, retry and backoff live below
this layer in the agent plumbing, and a synchronous pre-flight check could not know the outcome of
retries that have not happened yet. Two eligible judges of which one faults is `SingleJudge`; both
faulting is `PanelUnavailable`. The classification is therefore a fact about the run, not a
prediction about the provider.

### 2.11 Relationship to `LmWorkflow`

The harness deliberately does **not** build on `LmWorkflow`'s node graph, for structural reasons.

`LmWorkflow` is a **controller-LLM-driven** state machine, not a deterministic executor. Nothing
auto-advances: `WorkflowRuntime.AdvanceTo` (`src/LmWorkflow/Runtime/WorkflowRuntime.cs:1211`) is
called by the controller model via `SetCurrentNode`
(`src/LmWorkflow/Tools/WorkflowToolProvider.cs:98`), and the join state from
`WorkflowProjectionBuilder.BuildJoin` (`src/LmWorkflow/Runtime/WorkflowProjectionBuilder.cs:147`) is
**advisory** — its `satisfied` flag blocks nothing. `JoinMode.Quorum`
(`src/LmWorkflow/Model/WorkflowEnums.cs:48`) is rejected by the validator
(`src/LmWorkflow/Ingest/WorkflowValidator.cs:175`), `JoinPolicy.Threshold`
(`src/LmWorkflow/Model/JoinPolicy.cs:7`) is unused, and there is no reduce node (out-of-V1 at
`src/LmWorkflow/Model/WorkflowNode.cs:105`). A panel vote *is* a quorum join, so the primitive we
most need is the one V1 lacks — and putting a vote tally behind a controller LLM would make
aggregation non-deterministic, defeating the point of the artifact.

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

Eligibility filtering happens **before** construction, not inside it, so the degraded path never has
to violate an invariant:

```csharp
/// <summary>The outcome of filtering the configured judges against one candidate. A panel is
/// built from eligible judges; it never filters them itself.</summary>
public abstract record PanelComposition
{
    /// <summary>Two eligible judges of distinct families.</summary>
    public sealed record Full(IJudge First, IJudge Second) : PanelComposition;
    /// <summary>Exactly one eligible judge. Legal, and always yields Degradation = SingleJudge.</summary>
    public sealed record Degraded(IJudge Only, string Reason) : PanelComposition;
    /// <summary>No eligible judge. Yields NoDecision with PanelUnavailable.</summary>
    public sealed record Unavailable(string Reason) : PanelComposition;
}

public static class JudgePanel
{
    /// <summary>Pure, synchronous eligibility filter: drops judges whose ModelFamily equals
    /// candidate.GeneratorFamily (§3.2), then classifies what is left. It performs no I/O and
    /// probes no provider — provider failure is classified after fan-out (§2.12.6).</summary>
    public static PanelComposition Compose(IReadOnlyList<IJudge> configured, Candidate candidate,
        HarnessOptions options);
}
```

`JudgePanel` is a static helper because it holds no state and `JudgeGauntlet` calls it internally —
the gauntlet takes `IReadOnlyList<IJudge>` (§2.10), never a panel object, so nothing needs to inject
a panel instance.

`Compose` is **total**: it returns a composition for every input and throws for no candidate-driven
reason. The rules that *throw* are configuration-time, validated once when `JudgeGauntlet` is
constructed rather than per candidate:

- **Two judges** — the intended configuration — must have **distinct `ModelFamily`**. There is no
  override. An earlier draft carried an `AllowSameFamilyPanel` escape hatch; it is **removed**,
  because it permitted a panel `PanelComposition` cannot represent — `Full` is two judges of
  distinct families, and two same-family judges are neither that, nor `Degraded`, nor `Unavailable`.
  Giving it a fourth representation would have been the expensive way to close that hole, and it
  would have bought a configuration this document argues against three separate times: agreement
  between two same-family judges is false consensus, not signal (§2.12.2), one family cannot cancel
  a bias it shares (§2.12.4), and §2.12.5 already refuses a correlated second judge even at the cost
  of dropping to one. A flag whose only legal use is the failure mode next to it is not a flag.
- **One judge** is legal and is how the Revobot migration reproduces today's behaviour (§4.2). Every
  verdict it produces is `Degradation = SingleJudge` with a null `Dispersion`, by the same rule that
  covers a two-judge panel degraded at runtime — so a legacy single-judge row is never mistaken for a
  panel verdict in a #320 aggregate.
- **Zero, or more than two**, throws. Three-plus is the PoLL shape, which §2.9 explicitly did not
  buy.

That separation is the whole point. "Two judges of distinct families" is an invariant of the
*configuration*; "how many are eligible for this candidate" is a per-candidate fact that legitimately
varies, and `Degraded` is its honest representation rather than an exception.

When `candidate.GeneratorFamily` is null, only the **exclusion step** is skipped — no judge is
dropped — and classification then runs on the real eligible count exactly as above: two → `Full`,
one → `Degraded`, zero → `Unavailable`. It does **not** shortcut to `Full`. The Revobot adapter
is one judge and sets no `GeneratorFamily` (§4.2), and it must classify as `Degraded` like every
other one-judge configuration, or its documented `SingleJudge` verdict would be unreachable.

That a row was never eligibility-checked is therefore **not** carried by the composition. It is
carried by `experiment_record.generator_family` being NULL (§6.1), which is the durable form of the
same fact and the one §5.3 segments on.

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

1. **Arbiter escalation**, if `HarnessOptions.ArbiterJudge` is configured and its family is not the
   generator's. One call, made by `JudgeGauntlet.RunAsync` step 5 and fed back through a second
   reduction (§2.10) — the reducer never makes it. The arbiter's side decides; `Score` becomes the
   arbiter's weighted score, not a blend. Recorded as `"arbiter:<judgeId>:<family>"`.

   The arbiter is *intended* to be a stronger model than either panel member, but "stronger" is not
   enforced here: it needs an ordering over model strength, and that ordering is a cascade concern
   (§7). Configuring a weak arbiter is a configuration mistake this slice does not detect — recorded
   as a constraint on #322, which is where tier acquires a definition.
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
exists to prevent. Confidence is used only where it is meaningful: **within** a judge, as the abstain
floor (§2.8). It is deliberately **not persisted** (§6.1), so revisiting this needs a schema change
before it needs an argument — which is the honest price, and it is why §8.3's per-family reliability
fit is the cheaper route to the same end.

**Why `Split` is acceptable as a terminal outcome.** Verdicts are advisory in #319 (§1), so a `Split`
costs nothing operationally today. It is also the more useful datum: the **straddle rate is a direct
estimate of judge unreliability**, available without any human labels at all. A rising straddle rate
on a stable corpus says the rubric is underspecified at its threshold — precisely the diagnostic the
eval runner needs, and precisely what a forced resolution would erase. §5.3 therefore reports
straddle rate alongside `NoDecision` rate.

#### 2.12.4 What two judges cost in signal quality

Stated plainly so the decision is auditable later: **we trade error *correction* for error
*detection*.** PoLL's claim is that three small disjoint judges beat one large judge with less
intra-model bias and >7x lower cost (Verga et al., 2024, arXiv:2404.18796), and the mechanism is the
majority. An even panel has no majority, so a biased judge is not outvoted and a bias shared by both
families is not averaged out — it surfaces as a straddle, detected rather than corrected. The bill
is 2 calls plus an arbiter on the straddle rate rather than 3. What we keep is the part that matters
most early: two independent families disagreeing is a reliable alarm that the rubric or the candidate
is genuinely borderline, and that alarm is what §5.4 and §8.3 consume.

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

Verdicts are advisory in these slices (§1), so there is one rule rather than a mode switch:

- **One judge returns, one faults:** emit the verdict with `Degradation = SingleJudge`,
  `Dispersion = null`, and a `DegradationReason` naming the faulted family. It is a real verdict and
  is recorded, but §5 aggregates **exclude degraded rows by default** — from the numerator, not
  from the denominator (§5.3) — and report their count separately. One judge's read is worth more than a hole, provided the hole is labelled.
- **Both fault:** `NoDecision` with `Degradation = PanelUnavailable`. Never a `Pass`.

Retry and backoff for transient provider failures happen below this layer in the agent plumbing;
`Degradation` records only what the panel ended up with after those retries were exhausted, which is
why it is classified after fan-out (§2.10) rather than probed beforehand.

**What a future gate would need** is the opposite default — fail closed rather than degrade, because
a gate that silently weakens to one judge when a provider blips is how a quality bar erodes without
anyone deciding to lower it. That rule is not specified here (§1, Q7); what this slice provides is
the provenance it would need, since `Degradation` is persisted per verdict.

`ArbiterUnavailable` is the straddle case where escalation was configured but the arbiter could not
be reached: the outcome is `Split`, and the reason distinguishes "we chose not to escalate" from "we
tried and could not". That `Degradation` is non-null is stated once, on `Verdict` (§2.7).

**"We chose not to escalate" covers both arms of §2.12.3 rule 1 not holding** — no arbiter configured,
and an arbiter whose family is the generator's. Both make no call, so both are `Degradation = None`
with `"split:unresolved"`, and they are deliberately **not** given separate values. The discriminator
is already persisted: arbiter identity is fixed for a run (it is part of `EvaluatorConfigHash`,
§5.2), so the ineligible arm is exactly the straddle rows whose `generator_family` (§6.1) equals that
arbiter's family. That is the same derived-relation shape §3.2 uses for self-preference itself, which
is why neither needs a column of its own.

## 3. Bias controls

This section is the reason the numbers are worth anything. Each control names the failure mode it
defends against and where it is enforced.

### 3.1 Position and order bias

**The failure.** Swapping candidate order, GPT-4 is only 65.0% self-consistent (30% first-position
preference); GPT-3.5 46.2%; Claude-v1 23.8%, i.e. a 75% first-position preference. Renaming the
models barely helps (66.2%) (Zheng et al., 2023, arXiv:2306.05685). Reordering alone flipped a
leaderboard: Vicuna-13B "beat" ChatGPT on 66 of 80 queries under ChatGPT judging
(Wang et al., 2023, arXiv:2305.17926).

**Why it does not arise in these slices.** Position bias needs two or more candidates in one prompt
for one to occupy a favoured position. Every judge here is **pointwise** — one candidate, one rubric,
one score (§2.5) — so there is no ordering for a judge to prefer, and no presentation seed, peer
list, or paired runner is specified (§1). The one residual ordering, the order of rubric criteria, is
fixed by `Rubric.Criteria` being an ordered list under a versioned `RubricVersion`.

**The precondition on later work.** If a ranking story is ever raised, position bias becomes live
immediately and the mitigation is Balanced Position Calibration — run both orders and treat a
disagreement between the passes as no preference rather than a win (Wang et al., 2023,
arXiv:2305.17926). The numbers above are why that is not optional.

### 3.2 Self-preference bias

**The failure.** GPT-4 awards itself roughly +10% win rate and Claude-v1 roughly +25%
(Zheng et al., 2023, arXiv:2306.05685). It is causally tied to self-recognition: fine-tuning a
model's ability to recognize its own text moves self-preference linearly
(Panickssery et al., 2024, arXiv:2404.13076).

**Why this matters here specifically.** Revobot has this bug today, unmitigated. `JudgeAsync` builds
the judge's agent loop with `run.ModelId` — the model that generated the review under judgement:

```
samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs (JudgeAsync)
    await using var loop = _loopFactory.Create(
        profile, run.ModelId, ThreadId(run, DaemonAgentFactory.JudgeProfileId), ...);
```

So **the `JudgeAsync` path judged every review with the model that wrote it.**

The claim stops there deliberately. A v1 `judge` artifact persists only
`JudgeArtifactPayload(Score, Rationale, VariantId)` — **no judge-model provenance at all** — so it
cannot be established from the stored data that any *particular historical* artifact was
self-judged, only that the code path in force at the time does it. Historical rows are therefore
`unknown-provenance`, not `self-judged`, and §5 must not classify them as either.

> **Shipped (#326).** The judge model is now resolved through
> `IReviewAgentLoopFactory.ResolveEffectiveModelId`, configurable via
> `CodeReviewDaemon:JudgeModelId`, and recorded on every v2 row as `JudgeModelId` /
> `GeneratorModelId` / `SelfGraded`. It still **defaults** to the reviewer's own model, because
> changing what a recorded score means belongs with #322's tier rules — but the run now warns and the
> row now says so, so from v2 onward this axis is measured rather than argued. On the S2S transport
> the effective id is the configured `LmStreamingProviderId` (provision carries no per-call model
> field), and a `JudgeModelId` set there is refused at boot rather than silently discarded.

This is an independent argument for the schema v2 of §6.3: without a persisted judge model, the
self-preference axis is unmeasurable retrospectively, and no amount of later analysis recovers it.

**The judge half is not sufficient on its own.** Self-preference is a *relation* — judge family
equals generator family — so a persisted judge family measures nothing without the generator's
beside it. That is the claim forcing `experiment_record.generator_family` (§6.1) to exist as a
column rather than be derived: it is the second operand of this axis, and it doubles as the durable
marker for §2.12.1's unknown-family case, where NULL means the exclusion filter never ran.

**The controls.**

1. **Per candidate**, `JudgePanel.Compose` (§2.12.1) drops any judge whose `ModelFamily` equals
   `Candidate.GeneratorFamily`. It **filters**; it does not throw, because which judges are eligible
   is a property of the candidate and legitimately varies run to run. There is no override flag —
   a generator-family judge is never admitted (see §2.12.5 for why an override would be worse than a
   missing judge).

   The only generator-family rule that *throws* is at configuration time and is a different check:
   two configured judges sharing a family with **each other** (§2.12.1). That check governs
   judge-vs-judge; generator exclusion governs judge-vs-candidate. **Neither has an override flag**,
   and §2.12.1 records why the judge-vs-judge one lost the escape hatch it used to have. Permitting
   either is the same false-consensus failure, described at §2.12.5.
2. `Candidate.ModelId` is **never rendered into the judge prompt**. `Candidate.VariantId` is rendered
   only as an opaque label (`"A"` / `"B"`), never a model name — removing the self-recognition cue
   that drives the effect.
3. Panel family-disjointness (§2.12.1) means a self-preferring judge is **detected** — it straddles
   against the other family and the verdict becomes `Split`. Note this is weaker than the
   three-judge case, where such a judge would be outvoted and the verdict would still be correct;
   with two judges we get an alarm, not a correction (§2.12.4).
4. When excluding the generator's family would leave fewer than two judges, the harness runs the
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
Below 0.67 the rubric is treated as not yet fit for any use beyond exploratory measurement.

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
| Review dimensions (architecture, performance, tests, …) | prose in `samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml:128-135`; the real agent catalog is the **external `code-reviewer` plugin**, not this repo | **the model's own choice.** No dimension type in daemon code. |
| Dimension availability | `GatewaySkillProbe` → `GatewaySkillSupport(HasReviewSkill, ReviewerAgentCount, MarketplaceErrors)`, `samples/CodeReviewDaemon.Sample/Workspace/Sandbox/GatewaySkillProbe.cs:18`, `IsSupported` at `:25` | code — but it only checks `ReviewerAgentCount > 0`. It cannot tell *which* dimensions ran. |
| Sub-agent dispatch | the LLM calls the Agent tool itself | **not C#.** The daemon only waits: `ReviewSubAgentCompletionBarrier.WaitAsync`, `samples/CodeReviewDaemon.Sample/Agents/ReviewSubAgentCompletion.cs:258` |
| Result aggregation | `samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml:249-250` — "de-duplicate, drop anything a sub-agent retracted or could not substantiate" | **nothing.** One sentence of prose. The C# only renders an inventory: `ReviewSubAgentTreeSnapshot.ToSafeInventory()`, `samples/CodeReviewDaemon.Sample/Agents/ReviewSubAgentCompletion.cs:147` |
| Adversarial verification of findings | — | **does not exist.** No type takes a finding and challenges it. `ValidateReviewStillCurrentAsync` (`samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs:2934`) verifies the PR head SHA, not findings. |
| Severity Must/Should/Consider | `samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml:213` | prose only; never parsed. No severity enum in C#. |
| Grading | `JudgeAgent` (`samples/CodeReviewDaemon.Sample/Agents/JudgeAgent.cs:19`) + prompt `samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml:349-357` | code, but a single judge, a five-line prompt, an unanchored 0–10 scale, and **nothing consumes the score** |
| A/B arms | `ReviewVariant` (`samples/CodeReviewDaemon.Sample/Agents/ReviewVariant.cs:12`), `VariantReviewer` (`samples/CodeReviewDaemon.Sample/Agents/VariantReviewer.cs:24`) | code, and genuinely well-isolated |
| Capability denial | `OperationPolicy.Decide` (`samples/CodeReviewDaemon.Sample/Workspace/OperationPolicy.cs:121`), `ScopedToolFilter.Apply` (`samples/CodeReviewDaemon.Sample/Agents/ScopedToolFilter.cs:20`) | code. Solid. |
| Deterministic caps | input-side only: `MaxExistingCommentsListed = 120` (`samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs:2198`), `MaxKnowledgeEntries = 24` (`:1564`), `MaxKnowledgeDigestChars` (`:1567`), `UntrustedTranscriptText` caps (`samples/CodeReviewDaemon.Sample/Orchestration/ReviewNotesArtifactBuilder.cs:38`, `:41`) | code. But **no cap, dedupe, or path/line filter on findings.** |
| Retrieval bounding | `KnowledgeDigest.Deduplicate` (`samples/CodeReviewDaemon.Sample/Agents/KnowledgeDigest.cs:258`), `KnowledgeIndex.ParseIndex` | code. Deterministic ranking + caps. |

So the migration is less "move code" and more "give the prose a place to become code". The
code-level pieces that move are the judge, the variant arms, and the artifact shapes.

### 4.2 File by file

**New — `src/LmEval/`** (types per §2)

- `Candidate.cs`, `Gate.cs`, `Rubric.cs`, `Ballot.cs`, `Verdict.cs`, `JudgePanel.cs`,
  `JudgeGauntlet.cs`, `Aggregation/WeightedMeanAggregator.cs`,
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

- Build a `Candidate` from `JudgeRequest` (`samples/CodeReviewDaemon.Sample/Agents/JudgeAgent.cs:140`) with
  `TaskType = "code-review"`, `Content = JudgingInput`, `VariantId = request.VariantId`.
- Run a `JudgeGauntlet` configured with **zero gates and one judge** — the single-judge
  configuration §2.12.1 admits precisely for this. Every verdict it produces carries
  `Degradation = SingleJudge`, which is an accurate description of what Revobot does today and keeps
  these rows out of §5's headline aggregates until the panel actually lands in #322.
- Map `Verdict.Score` → the artifact's `score`, the single `Ballot.Reasoning` → `rationale`.
- **Preserve the malformed-response behaviour bit-for-bit for now.** Today `ParseVerdict` returns
  `(0, rawText)` on unparseable output (`:87`, `:110`). Under the harness that is naturally an
  abstention, which would change the persisted score from `0` to absent. Since this slice forbids
  behaviour change, `JudgeAgent` maps `Abstained → score 0, rationale = raw text` **and logs a
  warning naming the abstention**. The `0`-means-two-things defect becomes *visible* in logs without
  changing the artifact. Fixing it properly needs `judge` schema v2, scheduled in §6.3.

  > **Superseded (#327).** Schema v2 shipped ahead of the experiment record, so the `0` is no longer
  > invented: an unscored reply persists a null `Score` under `JudgeArtifactSchemaVersion = 2`. The
  > warning above stayed and still gates on `Score is null` rather than `Ballot.Abstained` — the
  > aggregator has two exclusion channels and only one sets `Abstained`. Rows written before the
  > bump keep their `1` and their ambiguous `0`, which is what the version field is for.
- Delete `ParseVerdict` (`:81`) and `UnwrapJson` (`:115`) — the harness's schema-validated parse
  replaces them. Their test coverage moves to `tests/LmEval.Tests` against the harness parser.

**`samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml`**
Add a `judge: v2.0` entry expressing the *same* grading intent as `v1.0` (`:349`) but rendered from a
`Rubric` — anchored criteria, reasoning-before-score. **`v1.0` stays and stays the default** in this
slice. `DaemonAgentFactory.CreateJudgeProfile` (`samples/CodeReviewDaemon.Sample/Agents/DaemonAgentFactory.cs:136`) selects the
version from config, so v2.0 ships dark and is switched on by an experiment in #321.

**`samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs`**
- `JudgeAsync` (`:3022`): no signature change. The `_loopFactory.Create(profile, run.ModelId, …)`
  call at `:3052` **stays as-is** — swapping the judge model is a behaviour change and is #322's job.
  A `// TODO(#322): judge shares the generator's model — self-preference, see P6 §3.2` comment
  records it at the line.
- `RunVariantArmAsync` (`:2993`): unchanged.
- The stage machine is untouched; `ReviewStage` (`samples/CodeReviewDaemon.Sample/Persistence/Models/ReviewRunAxes.cs:8`) keeps its
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
   model from the external plugin's methodology (`samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml:128`); the host only checks
   that *some* reviewer agents exist (`GatewaySkillSupport.ReviewerAgentCount`,
   `samples/CodeReviewDaemon.Sample/Workspace/Sandbox/GatewaySkillProbe.cs:25`). A `Rubric` is a fixed, versioned criterion list.
   These are different things. **Deliberate exclusion.** The harness judges *the review that came
   out*; it does not constrain which dimensions produced it. If dimension coverage later needs
   scoring, it becomes a `RubricCriterion` ("does the review address the dimensions the diff
   implicates?") — a judgement about coverage, not a host-side catalog.

2. **Findings as structured objects.** The review is Markdown prose; severity tags and file:line
   citations exist only as text conventions (`samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml:213`). Confirmed: there is no
   `Finding` / `ReviewResult` type anywhere. The closest is the *read-side*
   `ExistingReviewComment(Path, Line, Body, Author, IsActive, PublishedAt, ThreadId)` at
   `samples/CodeReviewDaemon.Sample/Orchestration/IReviewCommentPublisher.cs:76` — and its `Line` is a `string?` that is never
   parsed or validated. A per-finding gate (does this cited line exist? is this a duplicate?) needs a
   parsed finding model that does not exist today. **Extension, scheduled:** `RequiredAnchorGate` in
   #319 does the shallow version (citations present and well-formed); a real `ReviewFinding` parser
   is deferred to #320, where the eval runner needs it anyway to compare two reviews' findings.

3. **Cross-turn / conversational judgement.** Revobot's re-review path reasons about what changed
   since the previous round (prev head SHA, review round, computed in `ComputeRereviewContextAsync`).
   A `Candidate` is a single artifact with no history. **Deliberate exclusion for #319**; the corpus
   in #320 carries whole conversations, so that is the natural home. Recorded as Q5.

4. **The synthesis fold.** `samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml:249` folds N sub-agent outputs into one review,
   dropping unsubstantiated findings. That is a *reducer over candidates producing a new candidate*,
   not a judge producing a verdict. `IBallotAggregator` reduces ballots, not content.
   **Extension, deferred:** an `ICandidateReducer` is the obvious sibling interface and is the same
   shape `LmWorkflow` needs for its missing reduce node (§2.11). Not in #319; see Q6.

5. **Capability denial.** `OperationPolicy` (`samples/CodeReviewDaemon.Sample/Workspace/OperationPolicy.cs:121`) gates *what an agent
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
  `PromptTemplateHash` (`samples/CodeReviewDaemon.Sample/Persistence/Models/ReviewRun.cs:24`, `:30`, `:31`). Artifact kinds present:
  `review`, `review-provisional`, `review-context`, `review-synthesis-request`, `judge`,
  `b-variant-review`, `knowledge`. The payload shape is `ReviewArtifactPayload`
  (`samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs:3810`), and the input side is
  `ContextArtifactPayload(PrId, BaseSha, HeadSha, Diff, FileManifest, …)` (`:3782`) — an
  input/output pair by construction. **This is the best corpus we have and it is already
  accumulating.**
- **`b-variant-review` artifacts** (`samples/CodeReviewDaemon.Sample/Agents/VariantReviewer.cs:97`) — the collect-only B arm's output
  for the *same* input. Paired by construction, and the single most valuable rows in the corpus,
  because an A/B judgement needs exactly this shape.
- **Merged-PR outcome as a weak label.** `PrLifecycleState`
  (`samples/CodeReviewDaemon.Sample/Persistence/Models/ReviewRunAxes.cs:40`) says whether the PR
  eventually merged; it is genuinely computed by both providers
  (`samples/CodeReviewDaemon.Sample/Orchestration/GitHubPrProvider.cs:199-205`,
  `samples/CodeReviewDaemon.Sample/Orchestration/AdoPrProvider.cs:413-416`) and persisted at
  `samples/CodeReviewDaemon.Sample/Persistence/ReviewStore.cs:188`. Weak
  and confounded, but free. **Do not use `ReviewRun.MergeSha`** — it is declared and read back but
  never assigned by any path, so it is always NULL (§8.1.1).
- **Full conversation histories.** `CodeReviewDaemonOptions.ConversationStorePath`
  (`samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs:80`) wires a `FileConversationStore`
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
  (`samples/CodeReviewDaemon.Sample/Agents/ReviewFeedbackAgent.cs:21`) already distils per developer into
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
    /// <summary>How many of CorpusSize yielded a counted score, so the baseline's own coverage
    /// (ScoredItems/CorpusSize) is frozen beside the conditional metrics it belongs to. §5.3 forbids
    /// reporting MeanScore or P10Score without the coverage they were computed over, and a *frozen*
    /// conditional metric is not an exception to that rule — it is the case that most needs it,
    /// since the run it came from is long gone. Distinct from MinCoverage, which is a floor imposed
    /// on the candidate, not a fact about this baseline.</summary>
    public required int ScoredItems { get; init; }
    public required double MeanScore { get; init; }
    public required double P10Score { get; init; }
    public required double PassRate { get; init; }
    public required long MeanCostMicros { get; init; }   // host-supplied; see §6.2
    public required string CorpusSnapshotHash { get; init; }
    /// <summary>Identity of every score-affecting *evaluator* input (§5.4). A comparison across two
    /// different values is refused, not warned about.</summary>
    public required string EvaluatorConfigHash { get; init; }
    /// <summary>Least coverage a candidate run may have and still be compared (§5.3). It lives on
    /// the baseline so the run being judged cannot relax the bar it is judged against.</summary>
    public required double MinCoverage { get; init; }
}
```

A baseline is a **frozen tuple of (corpus snapshot, rubric version, variant config, evaluator
config)**. Recorded once, referenced thereafter, never recomputed silently.

The fourth element is the one the first three leave out. Corpus, rubric and variant hold the
*candidate* side fixed; nothing held the *evaluator* side fixed, and it moves on its own — §2.9
refits reliability weights from accumulating human verdicts, which is why §2.6 has to record the
weight that was actually applied. A refit alone changes scores, and against a frozen baseline that
reads as a candidate regression. `EvaluatorConfigHash` covers, in pipeline order: the **ordered gate
list, each gate's identity and its configuration**; each `(JudgeId, ModelId, ModelFamily, judge prompt
template hash)`; the arbiter's identity or its absence; the aggregator's `RuleId`; the
`HarnessOptions` that decide exclusion (`AbstainFloor`, `DispersionAlarm`); the id of the reliability
snapshot; and the human-signal source set that snapshot was fitted over (§8.1.5).

**The gates lead that list rather than trailing it, and their absence was the sharpest hole in it.**
A gate short-circuits to `Fail` with no score and no judge call (§2.10), and a gate-rejected item stays in
`PassRate`'s denominator while never entering its numerator (§5.3). Retuning one bound on one gate
therefore moves the reported pass rate with nothing about the candidate having changed — which is
the exact comparison this hash exists to refuse. Ordered, because gates short-circuit: the same set
in a different order rejects on a different gate and yields a different `gate_reason`.

### 5.3 What a run emits

`EvalRun` → one row per run plus one `Verdict` per corpus item, written to the same SQLite store as
the experiment record (§6). A run emits:

- per-item `Verdict` (score, gates, ballots, dispersion) joined to the cost the host recorded for
  that item's threads (§6.2) — the runner reads cost, the harness never produces it;
- aggregate mean / P10 / pass-rate, and total cost from that join;
- the **delta against the named baseline**, with a confidence interval;
- the count of `NoDecision` items — a run where the panel could not decide on 30% of items is not a
  clean result even if the remaining 70% look good;
- the **straddle rate** (§2.12.2) and, separately, the share of straddles resolved by arbiter versus
  left as `Split`. This is the primary judge-reliability signal available without human labels, and
  a rising straddle rate on a stable corpus means the rubric is underspecified at its threshold;
- the count of rows excluded from the aggregates by default: `Degradation != None` (§2.12.6), and
  `generator_family IS NULL`, which was never eligibility-checked (§2.12.1). Both are reported,
  neither is pooled with clean rows. **Excluded means excluded from the scored set and from the
  numerator — never from the denominator below.**

**The denominator, stated once, because omitting it is how a variant games this.** Every rate above,
and `PassRate`, is over **`CorpusSize`** — the item count of the named corpus snapshot, not the
count the run managed to process. An item that yielded no score still occupies the denominator and
never the numerator: a gate rejection (`Fail` with a null `Score`, §2.7), a `Split`, a `NoDecision`,
an excluded row, and an item that faulted or was never reached. Declining to score a hard item
therefore *lowers* `PassRate` instead of flattering it.

`MeanScore` and `P10Score` are the deliberate exception: they are **conditional**, defined only over
*scored* items — those that yielded a counted score and were not excluded. They are never reported
without `Coverage = scored / CorpusSize` beside them, and never carry a comparison alone: a mean over
a different subset is a different quantity, not a worse one.

### 5.4 Regression detection

A regression is declared when, against the named baseline on the same corpus snapshot and rubric
version:

1. `PassRate` drops by more than the configured margin **and** the drop falls outside a bootstrap 95%
   confidence interval over corpus items; **or**
2. `P10Score` drops materially while `MeanScore` holds — the tail-collapse case a mean hides; **or**
3. the `NoDecision` rate rises materially — the panel has stopped being able to judge, which
   invalidates the comparison rather than passing it.

**Comparisons are refused, not warned about, when `RubricVersion`, `CorpusSnapshotHash` or
`EvaluatorConfigHash` differ, or when the run's `Coverage` is below the baseline's `MinCoverage`.**
A silent incomparable comparison is the most likely way this system produces a confident wrong
number, so each is a hard error rather than a warning. The coverage bound and trigger 3 catch the
same failure at different ranges — the bound rejects a single thin run outright, the trigger
catches a coverage slide that stays inside it. A refusal is recorded on the run with the failing
condition named; it is not a regression and it is not a pass.

## 6. Experiment record (#321)

### 6.1 Schema

Three tables, stored in the daemon's existing SQLite database as **migration V5**.
`SchemaMigrations.All` currently runs 1–4
(`samples/CodeReviewDaemon.Sample/Persistence/Migrations/SchemaMigrations.cs:17-20`) with
`LatestVersion` derived at `:12`, so adding V5 is the established, tested path via `MigrationRunner`.

The split is forced by cardinality: one verdict has **many ballots** (§2.7) and may accumulate
**several human observations over time** (§8.1). Flattening either into the verdict row would lose
rows or leave the reader guessing which ballot a column described.

**`experiment_record` — one row per (candidate, variant, verdict).**

```
  id                    INTEGER PK
  experiment_id         TEXT NOT NULL   -- groups arms of one experiment
  review_run_id         INTEGER NULL    -- FK -> review_run.id; NULL for offline eval items
  eval_run_id           TEXT NULL       -- set for corpus replays
  task_type             TEXT NOT NULL
  variant_id            TEXT NOT NULL   -- 'primary' | 'b' | ...
  model_provider        TEXT NOT NULL   -- the generator's
  model_id              TEXT NOT NULL
  generator_family      TEXT NULL       -- NULL = unknown, which is also 'never eligibility-checked'
  reasoning_effort      TEXT NULL       -- NOT available from usage; see §6.2
  prompt_template_hash  TEXT NULL
  rubric_id             TEXT NOT NULL
  rubric_version        TEXT NOT NULL
  gate_outcome          TEXT NOT NULL   -- Pass | Reject | Inconclusive
  gate_reason           TEXT NULL
  judge_score           REAL NULL       -- NULL when NoDecision. NEVER 0-as-missing.
  judge_dispersion      REAL NULL       -- NULL, not 0.0, when undefined (§2.12.6)
  verdict_outcome       TEXT NOT NULL   -- Pass | Fail | Split | NoDecision
  tie_break_rule        TEXT NULL
  straddled             INTEGER NOT NULL -- 0/1; judges landed on opposite sides (§2.12.2)
  panel_degradation     TEXT NOT NULL   -- None | SingleJudge | PanelUnavailable | ArbiterUnavailable
  outcome               TEXT NULL       -- terminal business outcome, e.g. Merged | Abandoned
  generator_cost_micros INTEGER NULL    -- USD micro-units, matching UsageRecord
  cost_provenance       TEXT NOT NULL   -- Unavailable | PublicEstimate | ProviderReported
  input_tokens          INTEGER NULL
  output_tokens         INTEGER NULL
  candidate_length      INTEGER NOT NULL -- for the length-bias regression (§3.3)
  usage_thread_id       TEXT NULL        -- join key into the usage ledger (§6.2)
  created_at            TEXT NOT NULL
```

**`experiment_ballot` — one row per ballot, counted or excluded.**

```
  id                INTEGER PK
  record_id         INTEGER NOT NULL  -- FK -> experiment_record.id
  judge_id          TEXT NOT NULL
  judge_model_id    TEXT NOT NULL     -- provenance for §7.2's tier segmentation
  judge_family      TEXT NOT NULL
  role              TEXT NOT NULL     -- Panel | Arbiter
  score             REAL NULL         -- NULL when the judge abstained. NEVER 0-as-missing.
  abstained         INTEGER NOT NULL  -- 0/1
  applied_weight    REAL NULL         -- NULL for an excluded ballot
  exclusion_reason  TEXT NULL         -- NULL iff the ballot was counted
```

`ballot_count` and `excluded_ballot_count` are **not** columns; they are `COUNT(*)` over this table,
so the two can never disagree.

**`experiment_ballot` is a deliberately lossy tally summary, and this fixes the audit claims that may
be made of it.** It carries what reproduces the verdict's arithmetic and identifies every exclusion:
the score, the abstention, the applied weight, the reason, and both judge identities. It does **not**
carry per-criterion scores, `Confidence`, `AbstainReason`, or `Reasoning`. Three constraints follow,
and they are binding on the rest of this spec:

- **`exclusion_reason` is authoritative, never recomputed.** An abstain-floor exclusion is stored as
  its reason; `Confidence` is not stored, so no reader may re-derive the decision from the row and
  compare. A stored reason disagreeing with a re-derivation is not a case that can arise.
- **Calibration (§2.9) fits on `score` and the human verdict only.** Confidence-weighted or
  per-criterion fitting is not available from this schema, and revisiting the confidence question of
  §2.12.3 requires a schema change first — which is a real cost, and is the reason that section
  treats it as a follow-up rather than a near thing.
- **Rationale is not recoverable for eval rows.** For daemon rows it survives in the `judge` artifact
  (§6.3) for as long as artifact retention keeps it; for offline eval items (`eval_run_id` set,
  `review_run_id` NULL) there is no artifact and it is retained nowhere. No claim here depends on
  reading it back.

**`human_observation` — zero or more rows per record, append-only.**

```
  id             INTEGER PK
  record_id      INTEGER NOT NULL  -- FK -> experiment_record.id
  verdict        TEXT NOT NULL     -- Accepted | Edited | Rejected | Ambiguous | Ignored
  source         TEXT NOT NULL     -- provenance; see §8.1.3. NOT NULL and no default.
  confidence     REAL NULL         -- proxy strength in [0,1]; NULL for an explicit source
  edit_distance  INTEGER NULL
  observed_at    TEXT NOT NULL
```

Append-only is what makes Q8's answer safe: a proxy harvested today and an explicit control answered
next month are **two rows**, not an overwrite. Nothing decides at write time which wins, and a reader
wanting only explicit signal filters on `source`.

A provider that *cannot* produce the signal has not observed indifference, so it gets no row at all.
That case is recorded on the record instead:

```
  human_signal_state    TEXT NOT NULL   -- Observable | NotObservable
```

`Observable` with no `human_observation` rows means *nothing seen yet*; `NotObservable` means this
row's provider cannot produce the signal at all (§8.1.2). Collapsing the two would turn a provider
gap into apparent human indifference.

Indexes on `(experiment_id)`, `(task_type, rubric_version)`, `(review_run_id)`, `(usage_thread_id)`,
`experiment_ballot(record_id)`, `human_observation(record_id, source)`.

Cost is stored in **micro-units (integer)**, matching `UsageRecord.EstimatedPublicCostMicros` /
`ProviderReportedCostMicros`, so no floating-point drift is introduced at the boundary. Judge cost is
deliberately **not** a column: `LmEval` computes no cost (§2.1), and judge spend is recoverable from
the usage ledger by thread (§6.2) once that join exists.

NULL semantics follow the project's not-run-sentinel discipline: scores are NULL — never 0 — when
there is no decision or the judge abstained, `outcome` NULL means *not yet terminal*, and
`generator_family` NULL means *unknown*, never *same as the judge*. Every provenance enum
(`cost_provenance`, `panel_degradation`, `human_signal_state`, `human_observation.source`) is
**non-null with no default**, so a writer that ignores provenance fails to insert rather than
silently claiming a benign value.

A `CHECK` constraint ties `human_observation.confidence` to its source: NULL if and only if the
source is explicit, non-NULL otherwise. It is not possible to record a proxy verdict without
recording how strong the proxy was.

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
(used at `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs:3052` for the
judge and `:3015` for the B arm), so generator
and judge cost land on **different thread ids under the same review run** — which is exactly what
lets the two be separated by query, without `LmEval` having to know what a cost is (§2.1).

Three facts the implementer must know, because they change what §7 can be built on:

1. **Usage is not a SQL table.** It is persisted into `ThreadMetadata.Properties` as JSON strings
   under the keys `"usage.aggregate"` and `"usage.records"`
   (`src/LmMultiTurn/UsageAccounting/ConversationUsageProjection.cs:22`, `:25`), written through
   `IConversationStore.UpdateMetadataAsync`. Reading it for analysis means reading the conversation
   store, not joining a table — which is one more reason §6.1 denormalizes cost into
   `experiment_record`.
2. **Dollar estimation is no longer dead code, but only where a host asks for it.** `IPricingResolver`
   (`src/LmCore/Models/ModelPricing.cs:46`) has exactly one implementation,
   `PricingConfigResolver` (`src/LmConfig/Pricing/PricingConfigResolver.cs:12`); PR #365 registered it
   via `TryAddSingleton` in `RegisterLmConfigServices`
   (`src/LmConfig/Services/ServiceCollectionExtensions.cs:220`), and `LmStreaming.Sample/Program.cs:731,1969`
   resolves it and passes it into `MultiTurnAgentLoop`'s `pricingResolver` parameter. Any host that does
   not resolve `IPricingResolver` from DI and pass it through still leaves that parameter `null`, so
   `EstimatedPublicCostMicros` is still always null there and only `ProviderReportedCostMicros` is
   populated, from `UsageMessage.Usage.TotalCost` via
   `UsageRecordMapper.ToMicros` (`src/LmMultiTurn/UsageAccounting/UsageRecordMapper.cs:72`).
   **Slice #321 must confirm every host that produces `experiment_record` rows resolves and passes
   `IPricingResolver`** or every experiment row from a host that skips this, for a provider that does
   not self-report cost, carries `cost_provenance = Unavailable`, and #322 has nothing to optimize
   against.
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

`JudgeArtifactPayload` (`samples/CodeReviewDaemon.Sample/Agents/JudgeAgent.cs`) gains
`JudgeArtifactSchemaVersion = 2` with additive-optional provenance fields and a **nullable** `Score`,
retiring the `0`-means-parse-failure conflation from §4.2. The judge-model fields are what make the
self-preference axis (§3.2) measurable at all — v1 records no judge provenance, so every pre-v2 row
is permanently `unknown-provenance`. Readers must handle both versions;
`ReviewArtifact.ArtifactSchemaVersion`
(`samples/CodeReviewDaemon.Sample/Persistence/Models/ReviewArtifact.cs:17`) exists for exactly this.

**Shipped (#326, #327),** ahead of the experiment record rather than after it — the axis is
unmeasurable retrospectively, so every day spent on v1 rows is a day of data that cannot be
recovered:

| field | shipped | notes |
|---|---|---|
| `Score` | yes, **nullable** | null = the reduction produced no number; `0` is a real worst grade |
| `BallotCount` | yes | separates "no ballot survived" from "one ballot scored", which a null `Score` alone does not |
| `JudgeModelId` | yes | the **effective** id the transport resolved, never the requested one |
| `GeneratorModelId` | yes | second operand of the §3.2 relation; a judge id alone measures nothing |
| `SelfGraded` | yes | the relation itself, stated rather than derived. **Null**, never false, when either side is unrecorded |
| `Dispersion` | **deferred** | single-judge today (`Degradation = SingleJudge`), so dispersion over one ballot is not a number worth recording. Lands with the panel in #322 |
| `JudgeModelFamily` | **deferred** | no production family resolver exists. `SelfGraded` compares concrete ids ordinally, so two ids from one family read as independent. Lands with #322, which needs the resolver for §7.1(2) anyway |

`SelfGraded` is recorded rather than left to a reader joining the two id columns because a null on
either side must not collapse to "not self-graded": two unknowns are not evidence of independence,
and a reader who skips that subtlety would count such rows as clean.

## 7. Routing cascade (#322)

### 7.1 The mechanism

Today routing is six hand-tuned constants in
`samples/CodeReviewDaemon.Sample/Configuration/CodeReviewDaemonOptions.cs` (`:43`, `:49`, `:171`,
`:179`, `:211`, `:221`). The cascade replaces the *choice* of those values with a fit over
`experiment_record`, keeping the constants as the fallback when data is insufficient.

The shape follows FrugalGPT: a cascade in ascending cost order with a scoring function and a learned
per-stage threshold — stop if the score clears the threshold, else escalate. FrugalGPT matched GPT-4
accuracy at up to 98% cost reduction, or +4% accuracy at equal cost
(Chen, Zaharia & Zou, 2023, arXiv:2305.05176). RouteLLM's complementary pre-generation router,
trained on preference data with a cost-quality parameter, reported >85% cost reduction on MT-Bench
while retaining ~95% of GPT-4 performance (Ong et al., 2024, arXiv:2406.18665).

**No interface is specified here** (§1): the cascade executor does not exist yet and its shape
decides what a routing type would look like. What #322 inherits is two decisions and a data
dependency.

1. **Thresholds are fitted, not tuned.** The escalate-below-this-score threshold is fitted per task
   type from `experiment_record` — minimize expected cost subject to a pass-rate floor against the
   always-expensive baseline. Below the configured minimum record count the policy falls back to
   today's constant and **records that it did**, so an unfitted route is never mistaken for a fitted
   one.
2. **The generator may not share a judge family** (§2.12.5). With two judge families configured, a
   generator sharing one forces every run down to a single judge, silently degrading every verdict
   the cascade optimizes against. Validated where the cascade is configured — a whole-corpus
   degradation is a configuration error, not a runtime condition.

**Data dependency:** the effort axis depends entirely on §6.2(3). If `reasoning_effort` is not
stamped at write time, half of this fit has no input.

### 7.2 The guardrail: a cheap verifier rubber-stamps

This is the failure mode that would quietly destroy the whole engine, so it is enforced in code, not
documented as advice.

**The evidence.** With an imperfect verifier, resampling *cannot* reduce the false-positive rate — it
imposes a hard accuracy ceiling regardless of compute spent, and single-sample accuracy correlates
strongly with false-positive rate on HumanEval/MBPP (Stroebl, Kapoor & Narayanan, 2024,
arXiv:2411.17501). The generation-verification gap is real and scales with pretraining compute
(Song et al., 2024, arXiv:2412.02674): verifier capability is not free.

**The rules.**

1. **An all-cheaper-tier panel is a measured condition, not a silent one.** `LmEval` has no model
   tier concept (§1) and `JudgePanel` does not enforce one; a tier comparison belongs where models
   are configured. What the harness guarantees is the provenance to detect it: every ballot records
   its judge's model, so a #320 aggregate can segment runs whose judges were all cheaper than the
   generator instead of pooling them. Deciding what to *do* about that segment is #322.
2. **The counterweight is weighting, not permission.** Weak verifiers are not useless in aggregate:
   weak-supervision-*weighted* ensembles of <=70B judges reach o3-mini-level selection accuracy
   (87.7% avg) with a Llama-3.3-70B generator, and weighted significantly beats unweighted
   (Saad-Falcon et al., 2025, arXiv:2506.18203). So a cheap panel is permitted **only** as the
   full family-disjoint, reliability-weighted two-judge panel with an arbiter configured for
   straddles (§2.9, §2.12) — never a single cheap judge, and never a cheap panel running degraded.
   §5 already excludes `SingleJudge` rows from headline aggregates by default; #322 must not
   reintroduce them into a routing fit.
3. **A routing change must clear an eval gate.** No cascade threshold ships without a #320 run on the
   named baseline showing pass-rate within the configured margin. The routing policy is data-driven
   in both directions: the data proposes it, and the data has to ratify it.
4. **Cheap-gathers / expensive-judges is the asymmetry.** Gathering (read files, list changes, run a
   deterministic gate) is verifiable work whose output is checkable — route it cheap. Judgement is
   the step with no cheaper checker downstream — route it expensive. The cascade encodes that
   asymmetry rather than a global "use the cheap model more".

## 8. Human-edit feedback (#323)

### 8.1 Capturing the signal: proxy first, explicit later

**Decision (Q8, now closed): do both, proxy first.** Harvest a proxy verdict from signals the daemon
can observe, and add an explicit reviewer disposition control in a later slice. Waiting for the
explicit control would mean the first fitted judge weights arrive months after the harness does;
harvesting a proxy and never validating it would mean calibrating quality against a signal nobody
ever checked.

**The corpus cannot begin accruing on GitHub the day the harness ships.** Three of the four signals
below need provider work first. Mapping verdicts over signals that cannot be observed would produce
confidently wrong `Accepted` / `Rejected` / `Ignored` labels in exactly the table that calibrates the
judges, so §8.1.2 states what must be built first and §9 puts that work ahead of extraction.

#### 8.1.1 The candidate signals, and what each actually requires

| # | Signal | Status today | Evidence |
| --- | --- | --- | --- |
| S1 | A bot comment thread is **resolved** | **ADO only.** `ThreadIsActive` derives it from thread status (`samples/CodeReviewDaemon.Sample/Orchestration/AdoReviewCommentPublisher.cs:126`, `:184`). On **GitHub it is unavailable**: the publisher hardcodes `IsActive: true` at three sites (`samples/CodeReviewDaemon.Sample/Orchestration/GitHubReviewCommentPublisher.cs:140`, `:168`, `:185`) because the REST listing does not expose thread resolution at all | bot authorship via the `[{{ bot_name }}]` marker (`samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml:265`), detected at `samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs:2304` |
| S2 | The finding's **cited lines changed** in a later commit | **Available on both**, and the only signal needing no provider work — it is a git diff between the review's `HeadSha` and a later head, already computed for re-review context | needs the shallow `ReviewFinding` parser of §4.3(2) to resolve a finding to a line range (slice #320) |
| S3 | A bot comment was **deleted** | **Unavailable on both.** Absence from a later listing cannot prove deletion: the listing is capped at `MaxExistingCommentsListed = 120` (`samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs:2198`) and that cap is a *render* bound, not a retrieval bound. Nothing persists the set of comment ids the daemon published in a round | — |
| S4 | The thread was still **open at PR close/merge** | **Partly.** The PR's terminal state *is* real: `PrLifecycleState` is computed by both providers (`samples/CodeReviewDaemon.Sample/Orchestration/GitHubPrProvider.cs:199-205`, `samples/CodeReviewDaemon.Sample/Orchestration/AdoPrProvider.cs:413-416`) and persisted (`samples/CodeReviewDaemon.Sample/Persistence/ReviewStore.cs:188`). The *thread-open* half depends on S1, so S4 inherits S1's GitHub gap. Note `ReviewRun.MergeSha` is **never assigned by any path** — declared, inserted from the record, read back, and nothing ever sets it — so it must not be used as the merge signal | use `PrLifecycleState`, not `MergeSha` |

#### 8.1.2 What must be built first

Three prerequisites, none of them large, all of them ahead of extraction in §9:

- **P-A — GitHub thread resolution.** Fetch `reviewThreads { isResolved }` via the GraphQL API and
  populate `ExistingReviewComment.IsActive` from it instead of the hardcoded `true`. No GraphQL
  client exists in the daemon today. Until this lands, **S1 and S4 yield nothing on GitHub**, and
  since `Accepted` requires S1, no `Accepted` proxy row can be written for a GitHub PR at all.
- **P-B — Persisted comment-id snapshots.** Record, per review round, the set of comment ids the
  daemon published, so a later round can distinguish *deleted* from *beyond the render cap*. Without
  it S3 is unavailable, and it must not be approximated from the capped listing.
- **P-C — Populate the merge signal.** Either assign `ReviewRun.MergeSha` on the close path or make
  `PrLifecycleState` the sole documented merge signal. The latter is cheaper and is what §8.1.1
  recommends; a column nothing writes is worse than no column.

**The absence of a signal is never a verdict.** If a required signal is not observable for a row's
provider, the extractor writes **no `human_observation` row** and sets
`experiment_record.human_signal_state = 'NotObservable'` — never a guessed label. `Observable` with
no rows means "no human signal has appeared yet"; `NotObservable` means "this provider cannot produce
the signal, so absence here carries no information". Collapsing the two would silently convert a
provider gap into apparent human indifference, which is the same class of error as the
`0`-means-parse-failure defect in §2.6.

#### 8.1.3 Mapping signals to an observation

Applied only where §8.1.1 says the signal is observable for that row's provider. The mapping is
deliberately conservative: **only resolution *and* a code change earns `Accepted`.**

| Signals | `verdict` | `source` | `confidence` |
| --- | --- | --- | --- |
| S1 and S2 | `Accepted` | `ProxyResolvedAndChanged` | 0.8 |
| S1, not S2 | `Ambiguous` | `ProxyResolvedUnchanged` | 0.3 |
| S3 | `Rejected` | `ProxyDeleted` | 0.6 |
| S4 | `Ignored` | `ProxyIgnored` | 0.5 |
| a reviewer states it | `Accepted`/`Edited`/`Rejected` | `Explicit` | NULL |
| observable, nothing seen | *(no row)* | — | — |
| signal unavailable here | *(no row; `human_signal_state = 'NotObservable'`)* | — | — |

Note what the source column names: **the evidence, not the conclusion.**
`ProxyResolvedUnchanged` says "the thread was resolved and the code did not change" — a fact. It does
not say "the reviewer agreed". A later reader who disagrees with our inference can re-derive a
different verdict from the same recorded evidence, which would be impossible had we stored only
`Ambiguous`.

S2 alone is deliberately **not** mapped to anything. Code changing at a cited line, with no
resolution signal, is as easily unrelated churn as it is agreement, and on GitHub — where S1 is
unavailable until P-A — S2-alone would otherwise become the de facto acceptance signal for the whole
provider. That is precisely the contamination this section exists to prevent.

#### 8.1.4 Keeping the proxy's noise visible

A resolved thread genuinely means one of at least three things: *fixed*, *won't fix*, or *tidying up
a stale thread before merge*. The schema keeps that visible rather than flattening it:

1. **`Ambiguous` is a real value of `verdict`.** The resolved-but-unchanged case is not coerced
   into `Accepted` to make the column tidier. It is the largest noise source and it is labelled.
2. **`Ignored` is distinct from `Rejected`.** A thread nobody touched is not a reviewer disagreeing;
   conflating them would systematically understate agreement.
3. **`NotObservable` is distinct from "no rows yet"** (§8.1.2), so a provider gap never reads as
   indifference.
4. **`confidence` carries the strength**, so a fit can weight an `Accepted`-at-0.8 below an
   `Explicit` accept rather than treating them as the same observation.

#### 8.1.5 How a proxy verdict is never silently mixed with an explicit one

Three enforcement points, none of them conventional:

1. **Schema.** `human_observation.source` is `NOT NULL` with **no default**, plus the `CHECK` in §6.1
   tying `confidence` to it. A writer cannot omit provenance, and because the table is append-only a
   proxy observation is never overwritten by — or confused with — an explicit one on the same record.
2. **Default read path.** Judge calibration (§8.3) and every §5 aggregate read
   `source = 'Explicit'` **only**. Admitting proxy rows requires an explicit
   `IncludeProxyVerdicts` flag, and that flag is **stamped onto the resulting artifact** — a
   `JudgeReliability` row records the source set it was fitted from, so a weight derived from proxies
   is itself labelled and cannot later be mistaken for a human-validated one.
3. **Refusal to pool.** The eval runner refuses to compare or aggregate across differing
   `source` sets, exactly as it refuses across `RubricVersion` and
   `CorpusSnapshotHash` (§5.4). A hard error, not a warning, for the same reason: silent pooling is
   the most plausible route to a confident wrong number.

Once the explicit control ships, the proxy becomes independently checkable: on rows carrying **both**
an explicit verdict and a proxy-derived one, the proxy's agreement with the human is measurable. That
number is the proxy's own validation, and until it exists the confidence values in §8.1.3 are
declared estimates, not measurements.

### 8.2 Turning an edit into durable context

The pipeline exists and is well-built; #323 adds a source, not a mechanism.

`KnowledgeAgent` + `KnowledgeExtractionCommitter` write curated entries to
`KnowledgeBase/<scope>/<slug>.md` with `## UPDATES:` de-duplication and daemon-injected frontmatter.
`KnowledgeIndex` maintains `_index.jsonl` / `_toc.md`; `KnowledgeDigest` does host-side,
relevance-ranked retrieval against the PR's changed paths — `Deduplicate` at
`samples/CodeReviewDaemon.Sample/Agents/KnowledgeDigest.cs:258`, ranking at `:1683` — rendering each surviving entry with its exact
absolute path, under caps `MaxKnowledgeEntries = 24`
(`samples/CodeReviewDaemon.Sample/Orchestration/DaemonReviewStageExecutor.cs:1564`) and `MaxKnowledgeDigestChars`(`:1567`). PR #256
established that retrieval must be host-side and by exact path, because Grep-based retrieval silently
no-ops.

#323 adds an **edit-derived extraction pass**: where a human corrected the agent, the correction —
not the original finding — becomes the candidate lesson. Two rails, both inherited:

- **No path component comes from the model.** `ReviewFeedbackAgent`'s design rule
  (`samples/CodeReviewDaemon.Sample/Agents/ReviewFeedbackAgent.cs:14-18`) — the output path is derived host-side from the
  provider-reported identity — applies unchanged to edit-derived entries.
- **A knowledge entry is prior output, never a mandate.** `samples/CodeReviewDaemon.Sample/Prompts/daemon-prompts.yaml:220` already states
  that a KB lesson can inform a finding but never authorise a delivery or write action. Edit-derived
  entries have better provenance and get no more authority.

### 8.3 Closing the measurement loop

Human verdicts are what calibrate the judges. `JudgeReliability` (§2.9) is fitted per
`(JudgeId, TaskType, RubricVersion)` from agreement with recorded human observations, and §3.5's Krippendorff
alpha is computed against the same column. Until enough human verdicts accumulate, every judge weight
stays 1.0 and the harness reports its alpha as *not yet estimable* rather than computing a number
from four data points.

Both readers obey §8.1.5: they consume `source = 'Explicit'` by default, and any fit
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
`IJudge`/`Ballot`, `Verdict`, `JudgePanel`, `IBallotAggregator` + `WeightedMeanAggregator`,
`JudgeGauntlet`, `RubricJudge`, three starter gates; `tests/LmEval.Tests`; both in the solution.
Revobot's `JudgeAgent` re-seated on it per §4.2, plus the daemon's new project reference.

**Depends on:** nothing.

**Verified by:** (a) every existing test in `tests/CodeReviewDaemon.Sample.Tests` passing
**unmodified** — the no-behaviour-change proof; (b) harness unit tests covering at minimum: the
a two-judge same-family configuration throws at construction while a one-judge configuration does
not; a candidate whose `GeneratorFamily` matches a configured judge yields `Degraded`, not a throw;
an abstention is excluded rather than counted as zero; a first-`Reject` gate short-circuits with
**no model call** (asserted against a call-counting fake); `NoDecision` — not `Fail` — when no
ballot survives the abstain filter.

The two-panel logic of §2.12 needs its own cases: same-side scores decide without invoking the
arbiter (asserted against a call-counting arbiter fake); opposite-side scores escalate exactly once;
an unavailable arbiter yields `Split` with `Degradation = ArbiterUnavailable`, distinguishable from
the not-configured case; a straddle with an arbiter configured **in the generator's family** makes
**zero** arbiter calls and yields `"split:unresolved"` with `Degradation = None` — the case that has to
break if step 5 ever escalates on configuration alone; one judge faulting yields a verdict marked
`SingleJudge` with a **null** `Dispersion` (null, not 0.0 — a lone judge is not a panel in perfect
agreement); excluding the generator's family down
to one judge produces a `PanelComposition.Degraded` rather than throwing; and a single-judge
configuration — the Revobot adapter's shape — produces a `SingleJudge` verdict with a null
`Dispersion` and never reaches the arbiter; a candidate with a **null** `GeneratorFamily` and one
configured judge classifying as `Degraded` rather than `Full` (the adapter's exact shape — this is
the case that has to break if `Compose` ever shortcuts on a null family again); and every ballot in
`Verdict.Ballots` carrying a non-null `AppliedWeight` while every `ExcludedBallot` carries null.

Every bias control in §3 carries a mutation proof — the assertion must break when the control is
removed.

### Slice 2 — #320 · Eval runner

**Ships:** corpus reader over `review_run` / `review_artifact` (pairing `ContextArtifactPayload` input
with `ReviewArtifactPayload` output, and `b-variant-review` as the paired B arm), `EvalBaseline`, the
runner, the delta-and-regression report, and the hard refusal to compare across `RubricVersion`,
`CorpusSnapshotHash` or `EvaluatorConfigHash`, or below the baseline's `MinCoverage` (§5.4). The shallow `ReviewFinding` parser noted in §4.3(2).

**Depends on:** Slice 1.

**Verified by:** a replay over a fixture corpus reproducing a known baseline deterministically (using
`RecordPlaybackMiddleware` so no provider is called); a seeded regression from a deliberately
degraded variant detected; a cross-rubric-version comparison refused with a clear error; **a
comparison refused when only `EvaluatorConfigHash` differs** — same corpus, same rubric, same
candidate output, one judge model swapped, which must not read as a candidate regression — and
again when only a gate bound moved, since gates are in that hash (§5.2); **a run below the
baseline's `MinCoverage` refused rather than reported**; a tail-collapse case (mean flat, P10 down)
detected.

### Slice 3 — #321 · Experiment record

**Ships:** SQLite migration V5 per §6.1; the **host-side** write path that persists a `Verdict` and
its ballots (the harness itself writes nothing, §2.1);
**wiring `PricingConfigResolver` so cost is actually estimated** (§6.2(2)); effort stamped at write
time (§6.2(3)); `judge` artifact schema v2 (§6.3); `JudgeReliability` fitting.

**Depends on:** Slices 1 and 2.

**Verified by:** migration up-and-idempotent tests against the existing `MigrationRunner`; a
round-trip test proving `judge_score` is NULL — not 0 — for a `NoDecision`; a ballot round-trip
proving an abstention persists as `score IS NULL` with `abstained = 1` and an excluded ballot as
`applied_weight IS NULL`; a candidate judged with no known generator family persisting
`generator_family IS NULL` and being excluded from a §5.3 aggregate by default; a cost-attribution test
proving judge and generator spend separate cleanly by their distinct thread ids and sum to the
ledger's total; a test that `cost_provenance` is `PublicEstimate` once the pricing resolver is
wired (this is the regression guard against it silently reverting to dead code); a v1-artifact reader
still parsing after v2 ships.

### Slice 4 — #322 · Routing cascade

**Ships:** the cascade executor and its policy type, the threshold fit over `experiment_record`, the
insufficient-data fallback that names itself, the §7 configuration check that the generator never
shares a judge family, and the §7.2 guardrails (the weighted-panel rule and the eval gate on
threshold changes).

**Depends on:** Slice 3 — it needs accumulated records, and specifically needs the cost and effort
columns to be populated rather than null.

**Verified by:** a fit over a synthetic record set producing the known-optimal threshold; a
below-minimum-data case returning the constant **and** recording that it did; a cascade configured
with a generator sharing a judge family refused at startup; an end-to-end check that a threshold
change without a passing #320 run is rejected.

### Slice 5 — #323a · Provider signal enablement

**Ships:** the three prerequisites of §8.1.2, without which the proxy corpus cannot begin accruing on
GitHub at all — **P-A** GitHub thread resolution via GraphQL `reviewThreads { isResolved }`, feeding
`ExistingReviewComment.IsActive` instead of the hardcoded `true`; **P-B** persisted per-round
comment-id snapshots so deletion is distinguishable from the render cap; **P-C** a merge signal that
is actually written (adopt `PrLifecycleState`, and either populate or remove `ReviewRun.MergeSha`).

**Depends on:** nothing in this pillar — it is daemon plumbing and can run in parallel with Slices
1–3.

**Verified by:** a GitHub PR with a resolved bot thread surfacing `IsActive = false` (today it is
unconditionally `true`, so this test fails before the change and is the proof P-A actually landed); a
comment deleted between rounds distinguishable from one merely beyond the 120-entry render cap; the
merge signal non-null on a merged PR.

### Slice 6 — #323b · Proxy harvest

**Ships:** extraction of the observable signals (§8.1.1) into `human_observation` rows;
`human_signal_state` and the `confidence` `CHECK` constraint;
the `Explicit`-only default read path with the `IncludeProxyVerdicts` flag stamped onto any
`JudgeReliability` it produces; the eval runner's refusal to pool across source sets; and the
edit-derived knowledge extraction pass of §8.2.

**Depends on:** Slice 3 for the schema, **Slice 2** for the `ReviewFinding` parser that S2 needs to
resolve a finding to a line range, and **Slice 5** for S1/S3/S4 to be observable at all. Extraction
may ship for ADO ahead of P-A, since S1 is already real there — but the GitHub half is blocked.

**Verified by:** each signal combination in §8.1.3 mapping to the stated verdict and source; a
resolved-but-unchanged thread landing as `Ambiguous`, **not** `Accepted` (the flattening this slice
exists to prevent); `Ignored` distinct from `Rejected`; **a GitHub row with no resolution signal
landing as `human_signal_state = 'NotObservable'` with no observation row, not a guessed label** — the contamination guard, and the
one test that would have caught the original draft's error; S2 alone producing no verdict; an insert
omitting `source` failing rather than defaulting; a calibration fit over mixed sources
refusing to pool unless the flag is set, and stamping the flag on its output when it is; an edited
comment producing exactly one KB entry with a host-derived path, and a model-supplied path rejected.

### Slice 7 — #323c · Explicit reviewer disposition

**Ships:** the reviewer-facing disposition control, writing `source = 'Explicit'` with
`human_edit_distance`; and the proxy-validation report — on rows carrying both an explicit and a
proxy verdict, the proxy's measured agreement with the human (§8.1.5), which converts the confidence
values in §8.1.3 from estimates into measurements.

**Depends on:** Slice 6.

**Verified by:** an explicit observation landing **alongside** an earlier proxy observation on the
same record rather than replacing it, with the default read path returning only the explicit one; the agreement report computed only
over rows holding both; alpha and reliability reported as *not estimable* below the minimum sample
count rather than computed from a handful of rows.

## 10. Open questions

Q1, Q2 and Q8 are closed; their answers are worked through in §2.12 and §8.1 respectively. The
numbering of the remaining questions is left unchanged so existing references stay valid.

Several things this spec previously specified are now **deliberately unspecified** (§1) because they
depend on questions below: gating behaviour on Q7, pairwise judging on whether a ranking consumer is
ever raised, and the routing policy's interface on #322's cascade executor. Each is named in §1 with
its rationale rather than described in a shape nobody has committed to.

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
