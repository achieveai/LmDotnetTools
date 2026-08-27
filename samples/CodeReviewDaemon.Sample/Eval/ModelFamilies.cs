namespace CodeReviewDaemon.Sample.Eval;

/// <summary>
/// The daemon's <b>one</b> model-family rule (#456), referenced by every side that has to speak the
/// same language: <see cref="DaemonCorpusReader"/>, which stamps a candidate's generator family, and
/// <c>JudgeAgent</c>, which records a ballot's judge family.
/// <para>
/// <b>The rule.</b> A family is the <i>vendor of the underlying model</i>: the path segment
/// immediately before the model name in a <c>[router/]vendor/model</c> id.
/// </para>
/// <list type="table">
/// <item><term><c>openai/gpt-5</c></term><description><c>openai</c></description></item>
/// <item><term><c>anthropic/claude-opus-4.5</c></term><description><c>anthropic</c></description></item>
/// <item><term><c>openrouter/meta/llama-4</c></term><description><c>meta</c></description></item>
/// <item><term><c>openrouter/anthropic/claude-4</c></term><description><c>anthropic</c></description></item>
/// <item><term><c>gpt-5</c></term><description><b>null</b> — unknown</description></item>
/// </list>
/// <para>
/// <b>Why the vendor and not the router.</b> The family exists for exactly one decision: §7.1(2)'s
/// generator-family exclusion, which drops a judge that shares a family with the model that wrote
/// what it is grading. Reading the family as the <i>routing provider</i> — which is what a
/// first-segment rule returns — gets that decision wrong in both directions, and does it silently.
/// Everything routed through one gateway reads as one family, so a panel empties itself on every
/// candidate and reports <c>PanelUnavailable</c>, which looks like an outage rather than like a
/// misconfigured rule; and one vendor's model reached through two different routers reads as two
/// families, admitting the self-preferring judge that is precisely the case the rule exists to
/// catch. Self-preference is a property of the model, not of the wire it arrived on.
/// </para>
/// <para>
/// <b>Why no vendor table.</b> The rule is positional and derives nothing it was not told: no
/// dictionary of vendor names, no pattern matching on model names, no inference from a routing
/// provider. That is deliberate — see <see cref="Of"/> for what an unclassifiable id resolves to and
/// why guessing there is worse than answering "unknown".
/// </para>
/// <para>
/// <b>The honest limit.</b> A <i>two-segment</i> id is answered positionally like any other, so a
/// <c>router/model</c> id would yield the router. (Shorter ids are not: <see cref="Of"/>
/// short-circuits to null below two segments, and again when either of the two segments flanking the
/// FINAL slash is blank — a leading empty segment is not one of them, so <c>/anthropic/claude-4</c>
/// resolves to <c>anthropic</c> rather than to null.)
/// Closing that would need per-request provider metadata the daemon does not carry. Stated here
/// rather than papered over with a vendor list nobody maintains.
/// </para>
/// <para>
/// <b>Where this rule bites, and where it does not.</b> Under a configuration where every configured
/// model id is a bare slug, <see cref="Of"/> returns null for the generator, and
/// <c>JudgeAgent.JudgeFamilyOf</c> falls back to <see cref="Unresolved"/> for the judge — so
/// §7.1(2)'s generator-family exclusion never fires, and the rule is inert rather than wrong. Two
/// standing reasons push the daemon's ids that way: <c>CodeReviewDaemonOptions.VariantModelId</c>'s
/// doc records that the Copilot backend rejects OpenRouter-style slugs with
/// <c>model_not_supported</c>; and the judge side is not a model id at all, because
/// <c>S2SReviewAgentLoopFactory.ResolveEffectiveModelId</c> deliberately answers a colon-delimited
/// selector, <c>lmstreaming:&lt;providerId&gt;</c>, rather than a model. That second one is
/// structural, not a profile setting: <c>Program</c> refuses to boot without
/// <c>UseS2SReviewAgent</c>, the in-process factory having been removed.
/// </para>
/// <para>
/// Inert is the correct outcome, not a failure: nothing is misclassified because nothing is
/// classified. A null generator family is recorded as unknown, <c>JudgePanel</c> skips the exclusion
/// step on it, and <c>EvalRunner.Classify</c> can segment the row out as
/// <c>ScoreExclusion.UnknownGeneratorFamily</c> — though only if no earlier arm of that ordered
/// switch claims it first, since a gate rejection, an undecided or split verdict, and a degraded
/// panel are all matched before the family is consulted. Self-preference stays on the record either
/// way, from <c>JudgeArtifactPayload.SelfGraded</c>, which compares the two effective ids directly
/// and consults no family. The rule starts costing something the moment a slash-shaped id is
/// configured — which is when the two-segment hazard above stops being hypothetical.
/// </para>
/// </summary>
internal static class ModelFamilies
{
    /// <summary>
    /// The family recorded for a judge whose model id this rule cannot classify.
    /// <para>
    /// A sentinel exists at all because <c>IJudge.ModelFamily</c> is non-nullable — a family is what
    /// panel disjointness is validated on, and "no family" is not a configuration the panel can
    /// represent. It carries a <c>/</c> ON PURPOSE: <see cref="Of"/> returns a single path segment,
    /// which can never contain one, so this value cannot collide with a derived family and cannot
    /// therefore arm an exclusion against a judge nothing classified. Unknown stays unknown.
    /// </para>
    /// </summary>
    public const string Unresolved = "unresolved/family";

    /// <summary>
    /// The family of <paramref name="modelId"/>, or <b>null</b> when this rule cannot derive one.
    /// <para>
    /// Null is the load-bearing answer, not a shrug. A candidate's null generator family is recorded
    /// as <i>unknown</i>, skips the exclusion step entirely (§2.12.1) and is segmented out of the
    /// aggregates. Returning a guess instead — the bare id, or the routing provider — records a
    /// family that is <i>not the judge's</i>, which is the answer exclusion acts on: "we cannot
    /// classify this model" would silently become "this model is safe to grade", for every id whose
    /// vendor was never recorded.
    /// </para>
    /// </summary>
    /// <param name="modelId">A model id, or null when nothing recorded one.</param>
    public static string? Of(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var segments = modelId.Split('/', StringSplitOptions.TrimEntries);

        // Fewer than two segments is a bare model name with no vendor in it — unknown, never itself.
        if (segments.Length < 2)
        {
            return null;
        }

        var vendor = segments[^2];

        // A blank vendor OR a blank model name means the id is not the shape this rule reads, so it
        // resolves to unknown rather than to whatever happened to sit in that position.
        return string.IsNullOrEmpty(vendor) || string.IsNullOrEmpty(segments[^1]) ? null : vendor;
    }
}
