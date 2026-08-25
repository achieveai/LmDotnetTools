using System.Text.Json;
using AchieveAi.LmDotnetTools.LmEval;
using AchieveAi.LmDotnetTools.LmEval.Aggregation;
using AchieveAi.LmDotnetTools.LmEval.Judges;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Grades a completed review (plan §15, AC#7). The judge drives one collect-only run over an
/// <see cref="IMultiTurnAgent"/>, scores the reply through the shared LmEval harness, and
/// <b>persists only</b> a <c>judge</c> <see cref="ReviewArtifact"/> carrying exactly
/// <c>{score, rationale, variant_id}</c>.
/// <para>
/// "Judge feedback v1 = persist only": the verdict is recorded for later human inspection — it is
/// NEVER used to auto-route work, rewrite skills, or gate posting. The bounded payload shape is the
/// guardrail that keeps it that way.
/// </para>
/// <para>
/// This type is now an <b>adapter</b>, not a judge: parsing, abstention and scoring live in
/// <see cref="JudgeGauntlet"/> so Revobot and the offline gauntlet cannot drift into two different
/// definitions of what a score means. What stays here is everything Revobot-specific — the artifact
/// shape, the run-id log line, and the mapping from a harness verdict back onto the v1 integer
/// score this daemon has always persisted.
/// </para>
/// </summary>
internal sealed class JudgeAgent
{
    /// <summary>Schema version of the <c>judge</c> artifact payload (append-compatible).</summary>
    public const int JudgeArtifactSchemaVersion = 1;

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
    /// verdict through the harness, and persists a <c>judge</c> artifact holding only the score,
    /// rationale, and variant id.
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

        // Exactly one ballot exists: the transport below is an already-completed task over text
        // collected above, so the judge cannot fault. It is counted when the reply parsed and
        // excluded when it did not.
        var ballot = verdict.Ballots.Count > 0
            ? verdict.Ballots[0]
            : verdict.ExcludedBallots[0].Ballot;

        // A reply the harness could not read is an ABSTENTION, not a zero. v1 persists it as a
        // zero and continues to, because changing a persisted score is a data change — but the
        // warning makes the two distinguishable in the log, which they were not before.
        if (ballot.Abstained)
        {
            _logger.LogWarning(
                "Judge run {RunId} could not be read ({AbstainReason}) for variant '{Variant}'; "
                    + "recording score 0. This is an unscored reply, not a worst-possible review.",
                collected.RunId,
                ballot.AbstainReason,
                request.VariantId
            );
        }

        var score = verdict.Score is { } weighted
            ? (int)Math.Round(weighted, MidpointRounding.AwayFromZero)
            : 0;
        var rationale = ballot.Reasoning;

        // Persist ONLY {score, rationale, variant_id} — AC#7. No auto-routing, no skill rewriting.
        var payload = new JudgeArtifactPayload(score, rationale, request.VariantId);
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
    /// Runs the already-collected reply through the harness: no gates (v1 grades whatever the
    /// review stage produced) and one judge, whose prompt renderer is the identity so the bytes the
    /// model actually saw are the bytes recorded, not a re-render of them.
    /// <para>
    /// <see cref="Candidate.GeneratorFamily"/> is left null on purpose. Revobot's judge currently
    /// runs on the reviewing run's own model, so there is no second family to exclude and claiming
    /// one would assert an independence that does not hold — see the TODO at the judge stage.
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
                ModelId = request.Provider,
                ModelFamily = request.Provider,
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
    string JudgingInput);

/// <summary>The judge's persisted verdict plus the id of the <c>judge</c> artifact it was written to.</summary>
internal sealed record JudgeVerdict(int Score, string Rationale, string VariantId, long ArtifactId);

/// <summary>
/// The exact, bounded shape of a <c>judge</c> artifact payload: score, rationale, variant id — nothing
/// more (AC#7). New fields must be additive and optional to preserve append-compatibility.
/// </summary>
internal sealed record JudgeArtifactPayload(int Score, string Rationale, string VariantId);
