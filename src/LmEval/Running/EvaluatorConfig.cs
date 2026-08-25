using System.Security.Cryptography;
using System.Text;

namespace AchieveAi.LmDotnetTools.LmEval.Running;

/// <summary>
/// Everything on the <b>evaluator</b> side of a run that can move a score, frozen into one hash.
/// <para>
/// Corpus, rubric and variant hold the <i>candidate</i> side fixed; nothing held the evaluator side
/// fixed, and it moves on its own — reliability weights are refitted from accumulating human
/// verdicts, and a refit alone changes scores. Against a frozen baseline that reads as a candidate
/// regression. This hash is what turns that silent misreading into a refusal.
/// </para>
/// <para>
/// <b>The gates lead the hash rather than trailing it.</b> A gate short-circuits to a fail with no
/// score and no judge call, and a gate-rejected item stays in the pass rate's denominator while
/// never entering its numerator. Retuning one bound on one gate therefore moves the reported pass
/// rate with nothing about the candidate having changed — the exact comparison this hash exists to
/// refuse. Ordered, because gates short-circuit: the same set in a different order rejects on a
/// different gate.
/// </para>
/// </summary>
public sealed class EvaluatorConfig
{
    /// <summary>
    /// Field separator inside the hashed text. A unit separator rather than a printable
    /// character, so that no id or fingerprint can forge a field boundary and hash as a
    /// different configuration.
    /// <para>
    /// It does not, on its own, stop a value forging a <b>record</b> boundary: records here are
    /// newline-delimited. Every appended value therefore goes through <see cref="Field"/>, which
    /// refuses both characters outright. Refusal rather than escaping is available here because
    /// every value in this digest is an identifier or a fingerprint — a newline in one is
    /// pathological, unlike a candidate's prose, which is why the corpus digest length-prefixes
    /// instead.
    /// </para>
    /// </summary>
    private const char Separator = '\u001f';

    private EvaluatorConfig(
        IReadOnlyList<IGate> gates,
        IReadOnlyList<IJudge> judges,
        IBallotAggregator aggregator,
        HarnessOptions options,
        string reliabilitySnapshotId,
        IReadOnlyDictionary<string, double> reliabilityWeights,
        IReadOnlyList<string> humanSignalSources,
        string hash
    )
    {
        ReliabilityWeights = reliabilityWeights;
        Gates = gates;
        Judges = judges;
        Aggregator = aggregator;
        Options = options;
        ReliabilitySnapshotId = reliabilitySnapshotId;
        HumanSignalSources = humanSignalSources;
        Hash = hash;
    }

    /// <summary>The gates, in the order they run. The first reject short-circuits.</summary>
    public IReadOnlyList<IGate> Gates { get; }

    /// <summary>The configured panel.</summary>
    public IReadOnlyList<IJudge> Judges { get; }

    /// <summary>The reduction from ballots to a verdict.</summary>
    public IBallotAggregator Aggregator { get; }

    /// <summary>The abstain floor, the dispersion alarm and the optional arbiter.</summary>
    public HarnessOptions Options { get; }

    /// <summary>
    /// Identity of the reliability snapshot the run scores against. It is in the hash because a
    /// refit changes every weighted score without any candidate having changed.
    /// </summary>
    public string ReliabilitySnapshotId { get; }

    /// <summary>
    /// The human-signal source set the reliability snapshot was fitted over. In the hash for the
    /// same reason the snapshot id is: widening the source set refits the weights.
    /// </summary>
    public IReadOnlyList<string> HumanSignalSources { get; }

    /// <summary>
    /// The per-judge weights <see cref="ReliabilitySnapshotId"/> names, in the hash by <b>content</b>
    /// rather than by that id alone.
    /// <para>
    /// The id was the only thing hashed, and it is caller-supplied: a refit published under an id
    /// like <c>latest</c>, or one derived from a date that has not rolled, hashed identically to
    /// the weighting it replaced — and the one refusal that exists to stop a refit reading as a
    /// candidate regression did not fire. The id stays because it is what a human correlates back
    /// to a stored snapshot; the weights are what actually move the scores.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, double> ReliabilityWeights { get; }

    /// <summary>The frozen hash of every field above.</summary>
    public string Hash { get; }

    /// <summary>
    /// Freezes an evaluator configuration and computes its hash.
    /// </summary>
    /// <param name="gates">The gates, in execution order.</param>
    /// <param name="judges">One or two judges; two must be of distinct families.</param>
    /// <param name="aggregator">The reduction rule.</param>
    /// <param name="options">The harness options in force.</param>
    /// <param name="reliabilitySnapshotId">Identity of the reliability snapshot.</param>
    /// <param name="reliabilityWeights">
    /// The per-judge weights that snapshot holds; a judge absent from it weighs 1.0. Hashed by
    /// content, and checked against the weights the run is given.
    /// </param>
    /// <param name="humanSignalSources">Sources that snapshot was fitted over.</param>
    /// <exception cref="ArgumentException">
    /// A gate or judge does not implement <see cref="IConfigurationFingerprint"/> or returns null
    /// from it; the arbiter shares a <see cref="IJudge.JudgeId"/> with a panel judge; or a hashed
    /// id or fingerprint carries a character that could forge a boundary in the hashed text.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A reliability weight is NaN or outside [0,1], or an option bound is off its own scale.
    /// </exception>
    public static EvaluatorConfig Create(
        IReadOnlyList<IGate> gates,
        IReadOnlyList<IJudge> judges,
        IBallotAggregator aggregator,
        HarnessOptions options,
        string reliabilitySnapshotId,
        IReadOnlyDictionary<string, double> reliabilityWeights,
        IReadOnlyList<string>? humanSignalSources = null
    )
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(judges);
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reliabilityWeights);
        ArgumentException.ThrowIfNullOrWhiteSpace(reliabilitySnapshotId);

        // Where the configuration is FROZEN, not once per corpus item. A misfitted refit is a fact
        // about the whole run, and the per-item path records it as a faulted corpus item and
        // repeats that for every remaining one -- so the operator is told their corpus is
        // unscoreable when what was rejected is their configuration.
        AggregationContext.ValidateReliability(reliabilityWeights, nameof(reliabilityWeights));

        // Delegated rather than restated: panel size, family disjointness, judge-id uniqueness
        // and the arbiter-versus-panel id collision are one coherent set of configuration rules,
        // and holding a second copy here is how the two would drift into disagreeing about which
        // configurations are legal. The gauntlet this config builds runs the same check.
        JudgePanel.ValidateConfiguration(judges, options.ArbiterJudge);
        HarnessOptions.Validate(options, nameof(options));

        var arbiter = options.ArbiterJudge;

        var humanSources = (humanSignalSources ?? []).ToList();
        var weights = reliabilityWeights.ToDictionary(
            kv => kv.Key,
            kv => kv.Value,
            StringComparer.Ordinal
        );

        var builder = new StringBuilder();

        _ = builder.Append("gates\n");
        foreach (var gate in gates)
        {
            _ = builder
                .Append(Field(gate.GateId, "Gate id", gate.GateId))
                .Append(Separator)
                .Append(
                    string.Join(
                        TaskTypeSeparator,
                        gate
                            .AppliesTo.OrderBy(t => t, StringComparer.Ordinal)
                            .Select(t =>
                                Field(t, "Gate task type", gate.GateId, TaskTypeSeparator)
                            )
                    )
                )
                .Append(Separator)
                .Append(Field(FingerprintOf(gate, "Gate", gate.GateId), "Gate fingerprint", gate.GateId))
                .Append('\n');
        }

        _ = builder.Append("judges\n");
        foreach (var judge in judges)
        {
            _ = builder
                .Append(Field(judge.JudgeId, "Judge id", judge.JudgeId))
                .Append(Separator)
                .Append(Field(judge.ModelId, "Judge model id", judge.JudgeId))
                .Append(Separator)
                .Append(Field(judge.ModelFamily, "Judge model family", judge.JudgeId))
                .Append(Separator)
                .Append(
                    Field(
                        FingerprintOf(judge, "Judge", judge.JudgeId),
                        "Judge fingerprint",
                        judge.JudgeId
                    )
                )
                .Append('\n');
        }

        // The arbiter's ABSENCE is hashed as explicitly as its presence: swapping a configured
        // arbiter for none changes how every straddle resolves, and a hash that skipped the field
        // when it was null would call the two configurations equal.
        _ = builder.Append("arbiter\n");
        _ = builder
            .Append(
                arbiter is null
                    ? "none"
                    : $"{Field(arbiter.JudgeId, "Arbiter id", arbiter.JudgeId)}{Separator}"
                        + $"{Field(arbiter.ModelId, "Arbiter model id", arbiter.JudgeId)}{Separator}"
                        + $"{Field(arbiter.ModelFamily, "Arbiter model family", arbiter.JudgeId)}"
                        + $"{Separator}"
                        + Field(
                            FingerprintOf(arbiter, "Arbiter", arbiter.JudgeId),
                            "Arbiter fingerprint",
                            arbiter.JudgeId
                        )
            )
            .Append('\n');

        _ = builder
            .Append("aggregator\n")
            .Append(Field(aggregator.RuleId, "Aggregator rule id", aggregator.RuleId))
            .Append('\n');

        // Only the options that decide EXCLUSION. A dispersion alarm excludes nothing today, but it
        // is in the hash because the spec puts it there and because the field it flags is one a
        // reader segments on.
        _ = builder
            .Append("options\n")
            .Append(options.AbstainFloor.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
            .Append(Separator)
            .Append(
                options.DispersionAlarm?.ToString(
                    "R",
                    System.Globalization.CultureInfo.InvariantCulture
                ) ?? "none"
            )
            .Append('\n');

        // The snapshot id AND the weights it names. The count leads the entries so that a judge id
        // equal to a section header cannot shift where the next section is read to begin.
        _ = builder
            .Append("reliability\n")
            .Append(Field(reliabilitySnapshotId, "Reliability snapshot id", reliabilitySnapshotId))
            .Append(Separator)
            .Append(weights.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append('\n');

        foreach (var weight in weights.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            _ = builder
                .Append(Field(weight.Key, "Reliability weight judge id", weight.Key))
                .Append(Separator)
                .Append(
                    weight.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                )
                .Append('\n');
        }

        _ = builder
            .Append("human-signal-sources\n")
            .Append(
                string.Join(
                    Separator,
                    humanSources
                        .OrderBy(s => s, StringComparer.Ordinal)
                        .Select(s => Field(s, "Human signal source", s))
                )
            )
            .Append('\n');

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));

        return new EvaluatorConfig(
            gates,
            judges,
            aggregator,
            options,
            reliabilitySnapshotId,
            weights,
            humanSources,
            Convert.ToHexString(digest).ToLowerInvariant()
        );
    }

    /// <summary>Builds the gauntlet this configuration describes, so the hash names what ran.</summary>
    /// <param name="logger">Optional diagnostics for the gauntlet.</param>
    public JudgeGauntlet BuildGauntlet(
        Microsoft.Extensions.Logging.ILogger<JudgeGauntlet>? logger = null
    ) => new(Gates, Judges, Aggregator, Options, logger);

    /// <summary>
    /// Joins a gate's task types inside one field. A comma rather than the unit separator because
    /// the separator already means "next field" here; whichever character is chosen, a value
    /// carrying it is refused below, because a list delimiter forges a boundary exactly as a field
    /// delimiter does — <c>{"a,b"}</c> and <c>{"a","b"}</c> render the same bytes and gate
    /// different runs.
    /// </summary>
    private const char TaskTypeSeparator = ',';

    /// <summary>
    /// One value on its way into the digest, refused if it could forge a boundary there.
    /// <para>
    /// The unit separator argument covers field boundaries only. Records are newline-delimited and
    /// nothing was escaped, so a judge id or a host-supplied fingerprint carrying a newline — or a
    /// literal unit separator — could still make two different configurations hash the same, and
    /// the comparison refusal built on that hash would pass a genuinely incomparable pair.
    /// </para>
    /// <para>
    /// <paramref name="listSeparator"/> is supplied for a value that goes into the digest as one
    /// element of a joined list. Inside such a field the join character is a boundary of its own,
    /// so it is refused for the same reason and with the same consequence.
    /// </para>
    /// </summary>
    private static string Field(
        string value,
        string kind,
        string owner,
        char? listSeparator = null
    )
    {
        var forgesListBoundary = listSeparator is { } separator && value.Contains(separator);

        if (value.Contains('\n') || value.Contains(Separator) || forgesListBoundary)
        {
            var offending = forgesListBoundary
                ? $"a newline, a unit separator, or the comma '{listSeparator}' that joins its list"
                : "a newline or a unit separator";

            throw new ArgumentException(
                $"{kind} for '{owner}' contains {offending}. Any of them can forge a record, field "
                    + "or list boundary inside the evaluator config hash, making two different "
                    + "configurations hash identically — and the comparison refusal built on that "
                    + "hash would then pass a genuinely incomparable pair.",
                nameof(value)
            );
        }

        return value;
    }

    private static string FingerprintOf(object component, string kind, string id)
    {
        if (component is not IConfigurationFingerprint fingerprinted)
        {
            throw new ArgumentException(
                $"{kind} '{id}' ({component.GetType().Name}) does not implement "
                    + $"{nameof(IConfigurationFingerprint)}. Its configuration cannot enter the "
                    + "evaluator config hash, so a change to it would move scores without the hash "
                    + "moving — and the comparison refusal built on that hash would wave the change "
                    + "through as a candidate regression.",
                nameof(component)
            );
        }

        return fingerprinted.ConfigurationFingerprint
            ?? throw new ArgumentException(
                $"{kind} '{id}' ({component.GetType().Name}) reports a null configuration "
                    + "fingerprint, meaning it cannot describe its own score-affecting settings. "
                    + "That is refused rather than substituted with a constant, because a constant "
                    + "would hash two different configurations identically.",
                nameof(component)
            );
    }
}
