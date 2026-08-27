using System.Text.Json;
using AchieveAi.LmDotnetTools.LmEval;
using AchieveAi.LmDotnetTools.LmEval.Aggregation;
using AchieveAi.LmDotnetTools.LmEval.Judges;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Eval;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Grades a completed review (plan §15, AC#7). The judge drives one collect-only run over an
/// <see cref="IMultiTurnAgent"/>, scores the reply through the shared LmEval harness, and
/// <b>persists only</b> a <c>judge</c> <see cref="ReviewArtifact"/> carrying exactly the fields of
/// <see cref="JudgeArtifactPayload"/> — a closed set, not a minimum.
/// <para>
/// "Judge feedback v1 = persist only": the verdict is recorded for later human inspection — it is
/// NEVER used to auto-route work, rewrite skills, or gate posting. The bounded payload shape is the
/// guardrail that keeps it that way.
/// </para>
/// <para>
/// This type is now an <b>adapter</b>, not a judge: parsing, abstention and scoring live in
/// <see cref="JudgeGauntlet"/> so Revobot and the offline gauntlet cannot drift into two different
/// definitions of what a score means. What stays here is everything Revobot-specific — the artifact
/// shape, the run-id log line, and the mapping from a harness verdict back onto the integer score
/// this daemon persists — or onto no score at all, when the harness would not put a number on the
/// reply.
/// </para>
/// </summary>
internal sealed class JudgeAgent
{
    /// <summary>
    /// Schema version of the <c>judge</c> artifact payload (append-compatible). v2 per P6 §6.3: a
    /// <b>nullable</b> score, and the judge/generator provenance the self-preference axis needs.
    /// Readers must handle both versions — every v1 row is permanently unknown-provenance, and its
    /// <c>0</c> permanently ambiguous, which is exactly why the version field exists.
    /// </summary>
    public const int JudgeArtifactSchemaVersion = 2;

    public const string JudgeArtifactKind = "judge";

    /// <summary>
    /// The harness partition key for everything this daemon judges. Scores are comparable only
    /// within a task type, so every Revobot verdict shares one.
    /// </summary>
    internal const string JudgeTaskType = "code-review";

    /// <summary>
    /// The v1 scoring contract, restated as a rubric. It is deliberately a <b>single</b> criterion
    /// on the 0-10 scale: that is exactly what the shipped <c>judge: v1.0</c> prompt asks for, and a
    /// single-criterion rubric is what lets the harness accept that prompt's flat
    /// <c>{"score", "rationale"}</c> reply unchanged. Splitting it into real dimensions is a prompt
    /// change, and a prompt change is a rubric version bump — deferred to <c>judge: v2.0</c>.
    /// </summary>
    internal static readonly Rubric ReviewRubric = new()
    {
        RubricId = "revobot-review",
        RubricVersion = "1.0",
        TaskType = JudgeTaskType,
        MinScore = 0,
        MaxScore = 10,
        // Inert with one judge — nothing straddles a threshold on its own. Recorded so a second
        // judge can be added later without inventing a boundary at that moment.
        PassThreshold = 6,
        Criteria =
        [
            new RubricCriterion
            {
                CriterionId = "review-quality",
                Description =
                    "How well the review identifies real defects in the diff and states them so a "
                    + "maintainer can act, judged on the findings themselves rather than on length.",
                Anchors = new Dictionary<int, string>
                {
                    [0] = "no finding is correct, or the review restates the diff without judgement",
                    [5] = "some findings are correct and actionable; others are wrong or vague",
                    [10] =
                        "every finding is correct, cites where it applies, and is stated once "
                        + "without repetition",
                },
            },
        ],
    };

    /// <summary>
    /// No calibration data. Revobot runs one judge, so a reliability weight would multiply the only
    /// ballot there is and change nothing; the harness reads an absent judge as weight 1.0.
    /// </summary>
    private static readonly Dictionary<string, double> NoReliabilityData = [];

    private readonly IMultiTurnAgent _agent;
    private readonly ReviewStore _store;
    private readonly ILogger<JudgeAgent> _logger;

    public JudgeAgent(IMultiTurnAgent agent, ReviewStore store, ILogger<JudgeAgent> logger)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sends <paramref name="request"/>'s judging material as one user turn, scores the model's
    /// verdict through the harness, and persists a <c>judge</c> artifact holding exactly the fields of
    /// <see cref="JudgeArtifactPayload"/>: the score (or none), the rationale, the variant, and the
    /// judge/generator provenance §3.2 needs.
    /// </summary>
    public async Task<JudgeVerdict> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The turn is driven HERE, before the harness, for two reasons: the run id only exists on
        // this side of the collect and is load-bearing in the log line below, and a transport
        // failure must keep propagating to the caller rather than being caught by the gauntlet and
        // recorded as a judge fault.
        var collected = await AgentTextCollector
            .CollectAsync(_agent, request.JudgingInput, cancellationToken)
            .ConfigureAwait(false);

        var verdict = await ScoreAsync(request, collected.Text, cancellationToken)
            .ConfigureAwait(false);

        // At most one ballot exists: the transport below is an already-completed task over text
        // collected above, so the judge cannot fault today. That ballot is COUNTED when the harness
        // both read the reply and was willing to tally it, and EXCLUDED otherwise — abstention and
        // below-floor confidence are two SEPARATE exclusion channels and a ballot that parsed
        // perfectly can still land on the second one. Neither list is indexed unguarded: a gate, a
        // second judge or a real transport would let a verdict arrive carrying no ballot at all.
        var excluded = verdict.ExcludedBallots.Count > 0 ? verdict.ExcludedBallots[0] : null;
        var ballot = verdict.Ballots.Count > 0 ? verdict.Ballots[0] : excluded?.Ballot;

        // A verdict the harness would not put a number on is an UNSCORED reply, not a zero. v1
        // invented a 0 for it, and 0 is a legitimate worst score under this rubric — its 0 anchor
        // reads "no finding is correct" — so every stored verdict was ambiguous after the fact and
        // any aggregate over them silently contaminated. v2 persists no score at all. The warning
        // stays, and the condition is `Score is null`, deliberately NOT `ballot.Abstained`: that
        // predicate covers only one of the aggregator's two exclusion channels, so a reply carrying
        // a real score with low self-reported confidence would go unremarked.
        if (verdict.Score is null)
        {
            var unscoredReason = excluded is null
                ? verdict.TieBreakRule
                : excluded.Ballot.AbstainReason ?? excluded.ExclusionReason;

            _logger.LogWarning(
                "Judge run {RunId} produced no usable score ({UnscoredReason}) for variant "
                    + "'{Variant}'; recording no score. This is an unscored reply, not a "
                    + "worst-possible review.",
                collected.RunId,
                unscoredReason,
                request.VariantId
            );
        }

        int? score = verdict.Score is { } weighted
            ? (int)Math.Round(weighted, MidpointRounding.AwayFromZero)
            : null;

        // The raw reply is the fallback rationale for the ballot-less verdict guarded above; the
        // parser already falls back to it for every reply it could read but not score.
        var rationale = ballot?.Reasoning ?? collected.Text;

        // Persist the bounded v2 shape — AC#7 plus §6.3's provenance. No auto-routing, no skill
        // rewriting. The self-preference relation is stated rather than left to be derived: §3.2's
        // axis is unmeasurable retrospectively, so a reader who compares nothing must still be able
        // to see that a grade was issued by the model that wrote what it graded.
        var payload = new JudgeArtifactPayload(
            score,
            rationale,
            request.VariantId,
            request.JudgeModelId,
            request.GeneratorModelId,
            SelfGraded(request),
            verdict.Ballots.Count
        );
        var artifact = _store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = request.ReviewRunId,
            ArtifactSchemaVersion = JudgeArtifactSchemaVersion,
            ArtifactKind = JudgeArtifactKind,
            Provider = request.Provider,
            Payload = JsonSerializer.Serialize(payload),
        });

        _logger.LogInformation(
            "Judge run {RunId} graded variant '{Variant}' as {Score}; persisted judge artifact {ArtifactId}.",
            collected.RunId,
            request.VariantId,
            score,
            artifact.Id
        );

        return new JudgeVerdict(score, rationale, request.VariantId, artifact.Id);
    }

    /// <summary>
    /// Whether the grade was issued by the model that produced what it graded. <b>Null</b> when
    /// either model is unrecorded: that is unknown, never "no" — a row that cannot name both sides
    /// measures nothing on this axis, and §3.2 is explicit that a persisted judge identity without
    /// the generator's beside it is worth nothing.
    /// </summary>
    private static bool? SelfGraded(JudgeRequest request) =>
        request.JudgeModelId is { } judge && request.GeneratorModelId is { } generator
            ? string.Equals(judge, generator, StringComparison.Ordinal)
            : null;

    /// <summary>
    /// The judge's model family, under the daemon's one family rule (<see cref="ModelFamilies"/>).
    /// <para>
    /// This used to be <see cref="JudgeRequest.Provider"/>, which is not a model family and not even
    /// an LLM vendor: it is the <b>repo hosting provider</b> — <c>github</c> or <c>ado</c> — carried
    /// on the request so the artifact can name where the PR lives. Recorded as a family it read as
    /// one for every judge the daemon ran, so §7.1(2)'s exclusion compared a repo host against a
    /// model vendor and never fired; and had a host ever shared a name with a vendor it would have
    /// fired for no reason at all. Named and extracted so the derivation is assertable without
    /// standing up an agent turn (#456).
    /// </para>
    /// <para>
    /// A judge whose model id was never recorded resolves to
    /// <see cref="ModelFamilies.Unresolved"/> rather than to any stand-in that might match something:
    /// the sentinel cannot equal a derived family, so it arms no exclusion. Refuse to guess.
    /// </para>
    /// </summary>
    /// <param name="request">The judge request being graded.</param>
    internal static string JudgeFamilyOf(JudgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ModelFamilies.Of(request.JudgeModelId) ?? ModelFamilies.Unresolved;
    }

    /// <summary>
    /// Stand-in for a judge whose model id the run never recorded. <c>IJudge.ModelId</c> is
    /// non-nullable, so something must be written; this says plainly that nothing was, rather than
    /// naming a value — such as the repo hosting provider, which is what stood here — that a reader
    /// would take for the model that issued the grade.
    /// </summary>
    internal const string UnrecordedModelId = "unrecorded/model";

    /// <summary>
    /// The judge's model id, or <see cref="UnrecordedModelId"/> when the run recorded none.
    /// </summary>
    /// <param name="request">The judge request being graded.</param>
    internal static string JudgeModelIdOf(JudgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.IsNullOrWhiteSpace(request.JudgeModelId)
            ? UnrecordedModelId
            : request.JudgeModelId;
    }

    /// <summary>
    /// Runs the already-collected reply through the harness: no gates (v1 grades whatever the
    /// review stage produced) and one judge, whose prompt renderer is the identity so the bytes the
    /// model actually saw are the bytes recorded, not a re-render of them.
    /// <para>
    /// <see cref="Candidate.GeneratorFamily"/> is left null on purpose, and still NOT derived from
    /// <see cref="JudgeRequest.GeneratorModelId"/> — but the reason has changed, so it is restated
    /// rather than left standing (#456). It used to be that no production resolver mapped a model id
    /// to a family; <see cref="ModelFamilies.Of"/> now does, and this agent calls it for the judge's
    /// own side. The reason it is not called for the generator's is that arming §7.1(2)'s exclusion
    /// here would break this recorder rather than protect it: this is a deliberately single-judge
    /// harness, so any candidate whose generator matched the judge's family would leave zero eligible
    /// judges and every self-graded run would come back <c>NoDecision</c> instead of a grade. The
    /// exclusion belongs to the eval runner reading this corpus, where a panel exists to lose a member
    /// from.
    /// <br/>
    /// Whether the judge is independent of the generator is therefore still recorded as
    /// <see cref="JudgeArtifactPayload.SelfGraded"/> — a statement of fact about this run — rather than
    /// asserted here as a property the harness would then act on (#322).
    /// </para>
    /// </summary>
    private static Task<Verdict> ScoreAsync(
        JudgeRequest request,
        string reply,
        CancellationToken cancellationToken
    )
    {
        var candidate = new Candidate
        {
            CandidateId = $"{request.ReviewRunId}:{request.VariantId}",
            TaskType = JudgeTaskType,
            TaskInput = request.JudgingInput,
            Content = request.JudgingInput,
            VariantId = request.VariantId,
        };

        var judge = new RubricJudge(
            new RubricJudgeOptions
            {
                JudgeId = "revobot-judge",
                ModelId = JudgeModelIdOf(request),
                ModelFamily = JudgeFamilyOf(request),
                PromptRenderer = static (c, _, _) => c.Content,
            },
            (_, _) => Task.FromResult(reply)
        );

        var gauntlet = new JudgeGauntlet(
            gates: [],
            judges: [judge],
            aggregator: new WeightedMeanAggregator(),
            options: new HarnessOptions()
        );

        return gauntlet.RunAsync(candidate, ReviewRubric, NoReliabilityData, cancellationToken);
    }
}

/// <summary>
/// The material to judge and the run it belongs to. <see cref="VariantId"/> identifies which review
/// variant is being graded (e.g. <c>primary</c> or <c>b</c>) and is recorded verbatim in the artifact.
/// </summary>
internal sealed record JudgeRequest(
    long ReviewRunId,
    string Provider,
    string VariantId,
    string JudgingInput)
{
    /// <summary>
    /// The model the judge turn ran on. Recorded in the artifact so the self-preference axis is
    /// measurable at all — it cannot be recovered later.
    /// </summary>
    public string? JudgeModelId { get; init; }

    /// <summary>The model that produced the review being graded.</summary>
    public string? GeneratorModelId { get; init; }
}

/// <summary>The judge's persisted verdict plus the id of the <c>judge</c> artifact it was written to.</summary>
internal sealed record JudgeVerdict(int? Score, string Rationale, string VariantId, long ArtifactId);

/// <summary>
/// The exact, bounded shape of a <c>judge</c> artifact payload (AC#7, schema v2). New fields must be
/// additive and optional to preserve append-compatibility.
/// </summary>
/// <param name="Score">
/// The grade, or <b>null</b> when the harness would not put a number on the reply. Nullable rather
/// than a sentinel beside a non-null score: <c>0</c> is a legitimate worst grade under this rubric,
/// so a reader who skips a would-be <c>Unscored</c> flag would still see a <c>0</c> and be unable to
/// tell which one it is. A null cannot be misread that way.
/// </param>
/// <param name="Rationale">The judge's reasoning, or the raw reply when it could not be scored.</param>
/// <param name="VariantId">Which review variant was graded.</param>
/// <param name="JudgeModelId">The model that issued the grade. Null in a v1 row.</param>
/// <param name="GeneratorModelId">
/// The model that wrote what was graded. Null in a v1 row.
/// <para>
/// Both ids here are <b>provisioning identities</b> — whatever
/// <c>IReviewAgentLoopFactory.ResolveEffectiveModelId</c> answered — and on the S2S path that is the
/// selector <c>lmstreaming:&lt;providerId&gt;</c> rather than a per-call model. That is what
/// <see cref="JudgeArtifactPayload.SelfGraded"/> needs: two conversations provisioned under one
/// selector run one model, which is exactly the question that flag asks. It is deliberately NOT the
/// same value <see cref="Eval.DaemonCorpusReader"/> stamps on a candidate, which answers a different
/// question — which model wrote this text — and therefore prefers the escalated id recorded on the
/// review-provisional checkpoint. Two fields, two questions; do not "reconcile" them by making one
/// read the other, or the self-preference comparison starts reporting independence that is not there.
/// </para>
/// </param>
/// <param name="SelfGraded">
/// Whether those two are the same model — the self-preference axis of §3.2, stated rather than left
/// to be derived. Null when either side is unrecorded, which is unknown and never "no".
/// </param>
/// <param name="BallotCount">
/// How many ballots the reduction counted. Zero distinguishes "no ballot survived" from "one ballot
/// scored", which a null <see cref="Score"/> alone does not.
/// </param>
internal sealed record JudgeArtifactPayload(
    int? Score,
    string Rationale,
    string VariantId,
    string? JudgeModelId,
    string? GeneratorModelId,
    bool? SelfGraded,
    int BallotCount);
