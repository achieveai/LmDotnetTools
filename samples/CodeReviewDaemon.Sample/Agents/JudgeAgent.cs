using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Grades a completed review (plan §15, AC#7). The judge drives one collect-only run over an
/// <see cref="IMultiTurnAgent"/>, parses the model's verdict, and <b>persists only</b> a
/// <c>judge</c> <see cref="ReviewArtifact"/> carrying exactly <c>{score, rationale, variant_id}</c>.
/// <para>
/// "Judge feedback v1 = persist only": the verdict is recorded for later human inspection — it is
/// NEVER used to auto-route work, rewrite skills, or gate posting. The bounded payload shape is the
/// guardrail that keeps it that way.
/// </para>
/// </summary>
internal sealed class JudgeAgent
{
    /// <summary>Schema version of the <c>judge</c> artifact payload (append-compatible).</summary>
    public const int JudgeArtifactSchemaVersion = 1;

    public const string JudgeArtifactKind = "judge";

    private static readonly JsonSerializerOptions ParseOptions =
        new() { PropertyNameCaseInsensitive = true };

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
    /// Sends <paramref name="request"/>'s judging material as one user turn, collects the model's
    /// verdict, and persists a <c>judge</c> artifact holding only the score, rationale, and variant id.
    /// </summary>
    public async Task<JudgeVerdict> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var collected = await AgentTextCollector
            .CollectAsync(_agent, request.JudgingInput, cancellationToken)
            .ConfigureAwait(false);

        var (score, rationale) = ParseVerdict(collected.Text);

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
    /// Extracts <c>{score, rationale}</c> from the model's verdict. The judge is prompted to answer with
    /// a JSON object; a fenced <c>```json</c> block (if the model wrapped it) is unwrapped first. A
    /// missing score defaults to 0 and a missing rationale to the raw text, so a malformed verdict is
    /// still recorded rather than throwing.
    /// </summary>
    private static (int Score, string Rationale) ParseVerdict(string verdictText)
    {
        var json = UnwrapJson(verdictText);
        if (json.Length == 0)
        {
            return (0, verdictText.Trim());
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var score = root.TryGetProperty("score", out var scoreElement)
                && scoreElement.ValueKind == JsonValueKind.Number
                && scoreElement.TryGetInt32(out var parsedScore)
                ? parsedScore
                : 0;

            var rationale = root.TryGetProperty("rationale", out var rationaleElement)
                && rationaleElement.ValueKind == JsonValueKind.String
                ? rationaleElement.GetString() ?? string.Empty
                : verdictText.Trim();

            return (score, rationale);
        }
        catch (JsonException)
        {
            return (0, verdictText.Trim());
        }
    }

    /// <summary>Returns the JSON span of <paramref name="text"/>: a fenced block's body if present,
    /// otherwise the substring between the first <c>{</c> and last <c>}</c>, otherwise empty.</summary>
    private static string UnwrapJson(string text)
    {
        var trimmed = text.Trim();

        var fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var bodyStart = trimmed.IndexOf('\n', fenceStart);
            var fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (bodyStart > 0 && fenceEnd > bodyStart)
            {
                trimmed = trimmed[(bodyStart + 1)..fenceEnd].Trim();
            }
        }

        var open = trimmed.IndexOf('{');
        var close = trimmed.LastIndexOf('}');
        return open >= 0 && close > open ? trimmed[open..(close + 1)] : string.Empty;
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
