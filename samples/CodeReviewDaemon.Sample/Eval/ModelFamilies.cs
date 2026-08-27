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
/// <b>What this resolves to under the shipped configuration.</b> No model id the daemon ships is even
/// two segments. Every default in <c>CodeReviewDaemonOptions</c> is a bare slug —
/// <c>claude-sonnet-5</c>, <c>gpt-5.6-terra</c>, <c>claude-haiku-4.5</c> — as is every id in the
/// <c>achieveai</c>, <c>mcqdb</c> and <c>s2s</c> profiles (<c>gpt-5.6-luna</c>, <c>gpt-5.6-sol</c>,
/// <c>gpt-5.6-terra</c>, <c>claude-opus-4.8</c>); <c>VariantModelId</c>'s doc records why, namely that
/// the Copilot backend rejects OpenRouter-style slugs with <c>model_not_supported</c>. The judge side
/// is not a path at all: all three profiles set <c>UseS2SReviewAgent</c>, and
/// <c>S2SReviewAgentLoopFactory.ResolveEffectiveModelId</c> answers
/// <c>lmstreaming:&lt;providerId&gt;</c> — colon-delimited, deliberately a selector rather than a
/// model id. Slash-shaped ids therefore appear in this file's examples and in tests, and nowhere the
/// daemon ships.
/// </para>
/// <para>
/// So <see cref="Of"/> today returns null for the judge AND the generator on every shipped profile:
/// <c>JudgeAgent.JudgeFamilyOf</c> falls back to <see cref="Unresolved"/>, the candidate carries a
/// null generator family, and §7.1(2)'s exclusion never fires — <c>JudgePanel</c> skips the exclusion
/// step on a null family and <c>EvalRunner</c> segments the row out as
/// <c>ScoreExclusion.UnknownGeneratorFamily</c>. That is this rule answering honestly rather than
/// failing: nothing is misclassified because nothing is classified. Self-preference is still on the
/// record, from <c>JudgeArtifactPayload.SelfGraded</c>, which compares the two effective ids directly
/// and consults no family. The rule is inert under this configuration and starts costing something
/// the moment a slash-shaped id is configured — which is when the two-segment hazard above stops
/// being hypothetical.
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
