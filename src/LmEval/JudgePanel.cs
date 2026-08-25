namespace AchieveAi.LmDotnetTools.LmEval;

/// <summary>
/// The outcome of filtering the configured judges against one candidate. A panel is built from
/// eligible judges; it never filters them itself.
/// </summary>
public abstract record PanelComposition
{
    private PanelComposition() { }

    /// <summary>Two eligible judges of distinct families.</summary>
    /// <param name="First">The first eligible judge, in configuration order.</param>
    /// <param name="Second">The second eligible judge, in configuration order.</param>
    public sealed record Full(IJudge First, IJudge Second) : PanelComposition;

    /// <summary>
    /// Exactly one eligible judge. Legal, and always yields
    /// <see cref="PanelDegradation.SingleJudge"/>.
    /// </summary>
    /// <param name="Only">The one eligible judge.</param>
    /// <param name="Reason">Stable, non-sensitive text naming why only one is eligible.</param>
    public sealed record Degraded(IJudge Only, string Reason) : PanelComposition;

    /// <summary>
    /// No eligible judge. Yields <see cref="VerdictOutcome.NoDecision"/> with
    /// <see cref="PanelDegradation.PanelUnavailable"/>.
    /// </summary>
    /// <param name="Reason">Stable, non-sensitive text naming why nothing is eligible.</param>
    public sealed record Unavailable(string Reason) : PanelComposition;
}

/// <summary>
/// Eligibility filtering, which happens BEFORE a panel exists so the degraded path never has to
/// violate an invariant.
/// <para>
/// A static helper because it holds no state and <see cref="JudgeGauntlet"/> calls it internally —
/// the gauntlet takes a judge list, never a panel object, so nothing needs to inject a panel.
/// </para>
/// </summary>
public static class JudgePanel
{
    /// <summary>
    /// Families are compared case-insensitively throughout: a case-only difference is a spelling,
    /// not a second model family, and treating it as one would admit exactly the false-consensus
    /// panel the disjointness rule exists to forbid.
    /// </summary>
    internal static readonly StringComparer FamilyComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Pure, synchronous eligibility filter: drops judges whose <see cref="IJudge.ModelFamily"/>
    /// equals <see cref="Candidate.GeneratorFamily"/>, then classifies what is left. It performs no
    /// I/O and probes no provider — provider failure is classified after fan-out.
    /// <para>
    /// Total over candidates: every candidate yields a composition and none of them throws. It does
    /// validate its <paramref name="configured"/> list, because that is a configuration error
    /// rather than a candidate-driven one, and sharing the check with
    /// <see cref="JudgeGauntlet"/>'s constructor is what keeps the two from drifting apart.
    /// </para>
    /// </summary>
    /// <param name="configured">The configured judges. One or two, of distinct families.</param>
    /// <param name="candidate">The candidate to filter against.</param>
    /// <param name="options">The harness options in force.</param>
    /// <exception cref="ArgumentException">The configuration itself is invalid.</exception>
    public static PanelComposition Compose(
        IReadOnlyList<IJudge> configured,
        Candidate candidate,
        HarnessOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(options);
        ValidateConfiguration(configured);

        var generatorFamily = candidate.GeneratorFamily;

        // A null generator family skips the EXCLUSION step only. Classification still runs on the
        // real eligible count below, so a one-judge configuration stays Degraded rather than being
        // promoted to Full by an unknown family.
        var eligible = configured
            .Where(j =>
                generatorFamily is null || !FamilyComparer.Equals(j.ModelFamily, generatorFamily)
            )
            .ToList();

        var excludedCount = configured.Count - eligible.Count;
        var reason =
            excludedCount > 0
                ? $"generator-family-excluded:{generatorFamily}"
                : "single-judge-configured";

        return eligible.Count switch
        {
            0 => new PanelComposition.Unavailable($"generator-family-excluded:{generatorFamily}"),
            1 => new PanelComposition.Degraded(eligible[0], reason),
            _ => new PanelComposition.Full(eligible[0], eligible[1]),
        };
    }

    /// <summary>
    /// The rules that THROW, all of them configuration-time. Judge-vs-judge family distinctness
    /// governs the configuration; generator exclusion governs the candidate and only filters.
    /// Neither has an override flag: a same-family pair is the false-consensus failure, and it is a
    /// panel <see cref="PanelComposition"/> deliberately cannot represent.
    /// </summary>
    /// <param name="configured">The configured judges.</param>
    /// <exception cref="ArgumentException">The configuration is invalid.</exception>
    public static void ValidateConfiguration(IReadOnlyList<IJudge> configured)
    {
        ArgumentNullException.ThrowIfNull(configured);

        if (configured.Count is 0 or > 2)
        {
            throw new ArgumentException(
                $"A judge panel takes one or two judges; {configured.Count} were configured. "
                    + "Three or more is the panel-of-LLM-evaluators shape this harness did not buy.",
                nameof(configured)
            );
        }

        if (configured.Count == 2 && FamilyComparer.Equals(configured[0].ModelFamily, configured[1].ModelFamily))
        {
            throw new ArgumentException(
                $"Judges '{configured[0].JudgeId}' and '{configured[1].JudgeId}' share the same model family "
                    + $"'{configured[0].ModelFamily}'. Agreement between two same-family judges is false "
                    + "consensus, not signal, so a two-judge panel must be family-disjoint.",
                nameof(configured)
            );
        }
    }
}
