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
    /// </summary>
    private const char Separator = '\u001f';

    private EvaluatorConfig(
        IReadOnlyList<IGate> gates,
        IReadOnlyList<IJudge> judges,
        IBallotAggregator aggregator,
        HarnessOptions options,
        string reliabilitySnapshotId,
        IReadOnlyList<string> humanSignalSources,
        string hash
    )
    {
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
    /// <param name="humanSignalSources">Sources that snapshot was fitted over.</param>
    /// <exception cref="ArgumentException">
    /// A gate or judge does not implement <see cref="IConfigurationFingerprint"/> or returns null
    /// from it; or the arbiter shares a <see cref="IJudge.JudgeId"/> with a panel judge.
    /// </exception>
    public static EvaluatorConfig Create(
        IReadOnlyList<IGate> gates,
        IReadOnlyList<IJudge> judges,
        IBallotAggregator aggregator,
        HarnessOptions options,
        string reliabilitySnapshotId,
        IReadOnlyList<string>? humanSignalSources = null
    )
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(judges);
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(reliabilitySnapshotId);

        // Delegated rather than restated: panel size, family disjointness, judge-id uniqueness
        // and the arbiter-versus-panel id collision are one coherent set of configuration rules,
        // and holding a second copy here is how the two would drift into disagreeing about which
        // configurations are legal. The gauntlet this config builds runs the same check.
        JudgePanel.ValidateConfiguration(judges, options.ArbiterJudge);

        var arbiter = options.ArbiterJudge;

        var humanSources = (humanSignalSources ?? []).ToList();

        var builder = new StringBuilder();

        _ = builder.Append("gates\n");
        foreach (var gate in gates)
        {
            _ = builder
                .Append(gate.GateId)
                .Append(Separator)
                .Append(
                    string.Join(
                        ',',
                        gate.AppliesTo.OrderBy(t => t, StringComparer.Ordinal)
                    )
                )
                .Append(Separator)
                .Append(FingerprintOf(gate, "Gate", gate.GateId))
                .Append('\n');
        }

        _ = builder.Append("judges\n");
        foreach (var judge in judges)
        {
            _ = builder
                .Append(judge.JudgeId)
                .Append(Separator)
                .Append(judge.ModelId)
                .Append(Separator)
                .Append(judge.ModelFamily)
                .Append(Separator)
                .Append(FingerprintOf(judge, "Judge", judge.JudgeId))
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
                    : $"{arbiter.JudgeId}{Separator}{arbiter.ModelId}{Separator}{arbiter.ModelFamily}"
                        + $"{Separator}{FingerprintOf(arbiter, "Arbiter", arbiter.JudgeId)}"
            )
            .Append('\n');

        _ = builder.Append("aggregator\n").Append(aggregator.RuleId).Append('\n');

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

        _ = builder.Append("reliability\n").Append(reliabilitySnapshotId).Append('\n');

        _ = builder
            .Append("human-signal-sources\n")
            .Append(string.Join(Separator, humanSources.OrderBy(s => s, StringComparer.Ordinal)))
            .Append('\n');

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));

        return new EvaluatorConfig(
            gates,
            judges,
            aggregator,
            options,
            reliabilitySnapshotId,
            humanSources,
            Convert.ToHexString(digest).ToLowerInvariant()
        );
    }

    /// <summary>Builds the gauntlet this configuration describes, so the hash names what ran.</summary>
    /// <param name="logger">Optional diagnostics for the gauntlet.</param>
    public JudgeGauntlet BuildGauntlet(
        Microsoft.Extensions.Logging.ILogger<JudgeGauntlet>? logger = null
    ) => new(Gates, Judges, Aggregator, Options, logger);

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
