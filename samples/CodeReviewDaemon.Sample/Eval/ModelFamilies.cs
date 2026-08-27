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
/// <b>The honest limit.</b> An id that is not shaped <c>[router/]vendor/model</c> is answered
/// positionally too, so a two-segment <c>router/model</c> id would yield the router. Closing that
/// would need per-request provider metadata the daemon does not carry. Stated here rather than
/// papered over with a vendor list nobody maintains.
/// </para>
/// <para>
/// <b>What this actually resolves to today.</b> Nothing configured in this daemon is even two
/// segments. Every live model id is a bare Copilot slug — <c>gpt-5.6-luna</c>,
/// <c>claude-haiku-4.5</c> — and the effective judge id on the S2S path is
/// <c>lmstreaming:&lt;providerId&gt;</c>, colon-delimited and deliberately not a path. Slash-shaped
/// ids appear only in this comment and in tests; the Copilot backend rejects them outright with
/// <c>model_not_supported</c>. So <see cref="Of"/> returns null for the judge AND the generator on
/// every profile that ships, the judge falls back to <see cref="Unresolved"/>, the candidate carries
/// a null generator family, and §7.1(2)'s exclusion never arms.
/// </para>
/// <para>
/// That is this rule working, not failing: nothing is misclassified because nothing is classified,
/// and self-preference is still recorded — by <c>JudgeArtifactPayload.SelfGraded</c>, which compares
/// the two effective ids directly and does not consult a family at all. The two-segment hazard above
/// is therefore second-order, and this rule is scaffolding for the two-family panel of #322 rather
/// than a live guard. It starts costing something the moment a second judge family is configured.
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
