using AchieveAi.LmDotnetTools.LmEval.Aggregation;
using AchieveAi.LmDotnetTools.LmEval.Corpus;
using AchieveAi.LmDotnetTools.LmEval.Running;

namespace AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

/// <summary>
/// A judge that scores each candidate from a script keyed on candidate id, and declares a
/// configuration fingerprint so it can enter an <see cref="EvaluatorConfig"/>.
/// <para>
/// Separate from <see cref="FakeJudge"/> rather than an extension of it: the gauntlet tests need a
/// judge with one fixed score and a call counter, and the runner tests need one whose score varies
/// per corpus item. Folding both into one double would make every gauntlet test carry a script it
/// does not use.
/// </para>
/// </summary>
internal sealed class ScoringJudge : IJudge, IConfigurationFingerprint
{
    private readonly Func<Candidate, double?> _score;
    private readonly Func<Candidate, Exception?>? _fault;

    public ScoringJudge(
        string judgeId,
        string modelFamily,
        Func<Candidate, double?> score,
        string fingerprint = "v1",
        Func<Candidate, Exception?>? fault = null,
        string? modelId = null
    )
    {
        JudgeId = judgeId;
        ModelId = modelId ?? $"{modelFamily}/model";
        ModelFamily = modelFamily;
        _score = score;
        _fault = fault;
        ConfigurationFingerprint = fingerprint;
    }

    public string JudgeId { get; }

    public string ModelId { get; }

    public string ModelFamily { get; }

    public string? ConfigurationFingerprint { get; }

    /// <summary>Every candidate this judge was asked about, in call order.</summary>
    public List<string> SeenCandidateIds { get; } = [];

    public Task<Ballot> JudgeAsync(
        Candidate candidate,
        Rubric rubric,
        JudgeContext context,
        CancellationToken cancellationToken
    )
    {
        SeenCandidateIds.Add(candidate.CandidateId);

        if (_fault?.Invoke(candidate) is { } fault)
        {
            return Task.FromException<Ballot>(fault);
        }

        var score = _score(candidate);

        return Task.FromResult(
            new Ballot
            {
                JudgeId = JudgeId,
                ModelId = ModelId,
                ModelFamily = ModelFamily,
                CriterionScores = rubric.Criteria.ToDictionary(
                    c => c.CriterionId,
                    _ => (int)Math.Round(score ?? 0.0)
                ),
                WeightedScore = score ?? 0.0,
                Reasoning = $"{JudgeId} on {candidate.CandidateId}",
                Confidence = 0.9,
                Abstained = score is null,
                AbstainReason = score is null ? "declined" : null,
            }
        );
    }
}

/// <summary>
/// A gate that rejects any candidate whose content contains a marker, and can be told to throw.
/// Throwing is the case the runner's per-item isolation is about.
/// </summary>
internal sealed class MarkerGate : IGate, IConfigurationFingerprint
{
    private readonly string _marker;
    private readonly string? _throwOnCandidateId;

    public MarkerGate(
        string marker,
        string gateId = "marker",
        string? fingerprint = null,
        string? throwOnCandidateId = null,
        IEnumerable<string>? appliesTo = null
    )
    {
        _marker = marker;
        _throwOnCandidateId = throwOnCandidateId;
        GateId = gateId;
        ConfigurationFingerprint = fingerprint ?? $"marker={marker}";
        AppliesTo = new HashSet<string>(appliesTo ?? [], StringComparer.Ordinal);
    }

    public string GateId { get; }

    /// <summary>
    /// The task types this gate is scoped to. Settable because scoping is a hashed field: a gate
    /// narrowed to a task type the corpus does not carry stops running, which moves the pass rate
    /// with nothing about the candidate having changed.
    /// </summary>
    public IReadOnlySet<string> AppliesTo { get; }

    public string? ConfigurationFingerprint { get; }

    public ValueTask<GateDecision> EvaluateAsync(
        Candidate candidate,
        CancellationToken cancellationToken
    )
    {
        if (
            _throwOnCandidateId is not null
            && string.Equals(candidate.CandidateId, _throwOnCandidateId, StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException("gate blew up");
        }

        return ValueTask.FromResult(
            candidate.Content.Contains(_marker, StringComparison.Ordinal)
                ? GateDecision.Reject(GateId, "content carries the reject marker")
                : GateDecision.Pass(GateId, "content is clean")
        );
    }
}

/// <summary>
/// A gate that cancels the run's own token and then throws <see cref="OperationCanceledException"/>,
/// the way a real gate calling a cancelled downstream would.
/// <para>
/// This is the only way to reach the runner's cancellation filter. Cancelling <i>before</i> the run
/// starts is caught by the loop's own pre-item check and never touches the filter at all, so a test
/// built that way passes whether the filter is right, inverted, or absent.
/// </para>
/// </summary>
internal sealed class CancellingGate : IGate, IConfigurationFingerprint
{
    private readonly CancellationTokenSource _source;

    public CancellingGate(CancellationTokenSource source) => _source = source;

    public string GateId => "cancelling";

    public IReadOnlySet<string> AppliesTo { get; } = new HashSet<string>(StringComparer.Ordinal);

    public string? ConfigurationFingerprint => "cancels=true";

    public ValueTask<GateDecision> EvaluateAsync(
        Candidate candidate,
        CancellationToken cancellationToken
    )
    {
        _source.Cancel();
        throw new OperationCanceledException(_source.Token);
    }
}

/// <summary>
/// A gate that reads something outside itself — a checkout, a deployed schema file. When that thing
/// is gone it throws on <b>every</b> candidate, which is the environmental fault #401 is about: it
/// is only visible across the whole corpus, because every gate goes inconclusive on every item,
/// every item still scores a clean pass, and no aggregate that existed before moves at all.
/// <para>
/// Distinct from <see cref="MarkerGate"/>'s <c>throwOnCandidateId</c>, which is the one-flaky-item
/// case #352 contained.
/// </para>
/// <para>
/// <paramref name="checkoutPresent"/> switches the <b>environment</b> and nothing else, and the
/// fingerprint deliberately does not mention it: a fingerprint states how a gate was
/// <i>configured</i>, and whether the checkout it reads happens to exist on this machine is not a
/// configuration. So the healthy run and the outage run hash identically — which is the real
/// pairing (one deploy, one run before the checkout went missing and one after) and the only shape
/// in which a baseline can be frozen from the healthy run and the outage run refused against it
/// with no other refusal reachable.
/// </para>
/// </summary>
internal sealed class CheckoutGate(string gateId, bool checkoutPresent = false)
    : IGate,
        IConfigurationFingerprint
{
    public string GateId { get; } = gateId;

    public IReadOnlySet<string> AppliesTo { get; } = new HashSet<string>(StringComparer.Ordinal);

    public string? ConfigurationFingerprint { get; } = $"reads-checkout={gateId}";

    public ValueTask<GateDecision> EvaluateAsync(
        Candidate candidate,
        CancellationToken cancellationToken
    ) =>
        checkoutPresent
            ? ValueTask.FromResult(GateDecision.Pass(GateId, "the checkout resolved"))
            : throw new IOException("the checkout this gate reads is gone");
}

/// <summary>A gate that declares no configuration, for the refusal path.</summary>
internal sealed class OpaqueGate : IGate
{
    public string GateId => "opaque";

    public IReadOnlySet<string> AppliesTo { get; } = new HashSet<string>(StringComparer.Ordinal);

    public ValueTask<GateDecision> EvaluateAsync(
        Candidate candidate,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(GateDecision.Pass(GateId, "always"));
}

/// <summary>Shared setup for the eval-runner tests.</summary>
internal static class EvalFixtures
{
    public const string RejectMarker = "REJECT-ME";

    public static Candidate Item(
        string id,
        string content = "a review",
        string? generatorFamily = "meta"
    ) =>
        new()
        {
            CandidateId = id,
            TaskType = HarnessFixtures.TaskType,
            TaskInput = "Grade this code review:",
            Content = content,
            GeneratorFamily = generatorFamily,
        };

    public static CorpusSnapshot Snapshot(params Candidate[] items) =>
        CorpusSnapshot.Create("corpus-1", items);

    public static EvaluatorConfig Config(
        IReadOnlyList<IGate>? gates = null,
        IReadOnlyList<IJudge>? judges = null,
        HarnessOptions? options = null,
        string reliabilitySnapshotId = "snap-1",
        IBallotAggregator? aggregator = null,
        IReadOnlyDictionary<string, double>? reliabilityWeights = null
    ) =>
        EvaluatorConfig.Create(
            gates ?? [],
            judges
                ?? [
                    new ScoringJudge("j-a", "anthropic", _ => 8.0),
                    new ScoringJudge("j-b", "google", _ => 8.0),
                ],
            aggregator ?? new WeightedMeanAggregator(),
            options ?? new HarnessOptions(),
            reliabilitySnapshotId,
            reliabilityWeights ?? new Dictionary<string, double>()
        );

    /// <summary>A run over the given items with a single non-generator-family judge.</summary>
    public static Task<EvalRun> RunAsync(
        EvaluatorConfig config,
        CorpusSnapshot snapshot,
        EvalCostSource? costSource = null
    ) =>
        new EvalRunner(config).RunAsync(
            "run-1",
            snapshot,
            HarnessFixtures.Rubric(),
            new Dictionary<string, double>(),
            costSource,
            CancellationToken.None
        );
}
