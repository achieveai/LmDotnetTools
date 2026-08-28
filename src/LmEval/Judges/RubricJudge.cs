using AchieveAi.LmDotnetTools.LmMultiTurn;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmEval.Judges;

/// <summary>Drives one judging turn and returns the model's raw reply text.</summary>
/// <param name="prompt">The rendered judging turn.</param>
/// <param name="cancellationToken">Cancellation.</param>
public delegate Task<string> JudgeReplyTransport(string prompt, CancellationToken cancellationToken);

/// <summary>How one <see cref="RubricJudge"/> identifies itself and renders its prompt.</summary>
public sealed record RubricJudgeOptions
{
    /// <summary>Stable identity, recorded on every ballot and fault.</summary>
    public required string JudgeId { get; init; }

    /// <summary>The model this judge runs on.</summary>
    public required string ModelId { get; init; }

    /// <summary>
    /// The model's family. Panel disjointness and generator exclusion are both decided on it.
    /// </summary>
    public required string ModelFamily { get; init; }

    /// <summary>
    /// How the judging turn is rendered. Defaults to the rubric-rendering
    /// <see cref="RubricPromptRenderer"/>; a host that already owns a rendered prompt — the Revobot
    /// adapter does — supplies its own so the bytes it sends do not change.
    /// </summary>
    public Func<Candidate, Rubric, JudgeContext, string> PromptRenderer { get; init; } = RubricPromptRenderer.Render;

    /// <summary>
    /// Stable identity of the prompt template <see cref="PromptRenderer"/> renders, for the
    /// evaluator config hash. Leave it null when the renderer is the default one — its identity is
    /// then known from the type itself.
    /// <para>
    /// A host supplying its own renderer <b>must</b> supply this. The renderer is an opaque
    /// delegate, so nothing can recover from it which bytes it will send; a run configured with a
    /// custom renderer and no hash therefore cannot describe its own evaluator side, and
    /// <c>EvaluatorConfig</c> refuses it rather than hashing two different prompts identically.
    /// </para>
    /// </summary>
    public string? PromptTemplateHash { get; init; }

    /// <summary>
    /// Stable identity of every score-affecting setting on the transport this judge drives —
    /// sampling temperature, top-p, the concrete deployment behind <see cref="ModelId"/>, a system
    /// prompt the host prepends. Required for the judge to enter an <c>EvaluatorConfig</c>.
    /// <para>
    /// The transport is a <see cref="JudgeReplyTransport"/>: an opaque delegate holding no field
    /// the judge could read back. Two judges over the same model at different temperatures produce
    /// different ballots, and with nothing declared they produced the same fingerprint and
    /// therefore the same evaluator hash — so a temperature change read as a candidate regression.
    /// Only the host that built the transport knows this, so only the host can state it, and a
    /// judge that has not been told refuses rather than hashing under a constant.
    /// </para>
    /// </summary>
    public string? TransportFingerprint { get; init; }

    /// <summary>True when <see cref="PromptRenderer"/> is still the built-in one.</summary>
    internal bool UsesDefaultRenderer =>
        PromptRenderer.Method == DefaultRenderer.Method && PromptRenderer.Target == DefaultRenderer.Target;

    private static readonly Func<Candidate, Rubric, JudgeContext, string> DefaultRenderer = RubricPromptRenderer.Render;
}

/// <summary>
/// The default judge: render the rubric into a turn, send it, parse the reply into a ballot.
/// <para>
/// It does not own the agent it drives. The host owns the agent, its lifetime and its thread id —
/// which is the same reason the harness carries no cost type and takes no usage sink.
/// </para>
/// </summary>
public sealed class RubricJudge : IJudge, Running.IConfigurationFingerprint
{
    private readonly RubricJudgeOptions _options;
    private readonly JudgeReplyTransport _transport;
    private readonly ILogger<RubricJudge>? _logger;

    /// <summary>Builds a judge over an arbitrary transport. The seam tests and adapters use.</summary>
    /// <param name="options">Identity and prompt rendering.</param>
    /// <param name="transport">Drives one judging turn.</param>
    /// <param name="logger">Optional diagnostics.</param>
    public RubricJudge(RubricJudgeOptions options, JudgeReplyTransport transport, ILogger<RubricJudge>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logger = logger;
    }

    /// <summary>
    /// Builds a judge over an <see cref="IMultiTurnAgent"/> — the default transport. The agent's
    /// lifetime stays with the caller: this judge drives it and never disposes it.
    /// </summary>
    /// <param name="agent">The agent to drive.</param>
    /// <param name="options">Identity and prompt rendering.</param>
    /// <param name="logger">Optional diagnostics.</param>
    public static RubricJudge Over(
        IMultiTurnAgent agent,
        RubricJudgeOptions options,
        ILogger<RubricJudge>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(agent);
        return new RubricJudge(
            options,
            async (prompt, ct) => (await AgentTextCollector.CollectAsync(agent, prompt, ct).ConfigureAwait(false)).Text,
            logger
        );
    }

    /// <summary>
    /// This judge's score-affecting configuration: which prompt template it renders and how its
    /// transport is configured. Null — and therefore refused by <c>EvaluatorConfig</c> — when
    /// either is undeclared, since a constant substituted for an unknown hashes two different
    /// configurations identically. See <see cref="RubricJudgeOptions.PromptTemplateHash"/> and
    /// <see cref="RubricJudgeOptions.TransportFingerprint"/>.
    /// </summary>
    public string? ConfigurationFingerprint
    {
        get
        {
            var prompt =
                _options.PromptTemplateHash is { } declared ? declared
                : _options.UsesDefaultRenderer ? $"builtin:{nameof(RubricPromptRenderer)}"
                : null;

            return prompt is null || _options.TransportFingerprint is not { } transport
                ? null
                : $"prompt={prompt};transport={transport}";
        }
    }

    /// <inheritdoc />
    public string JudgeId => _options.JudgeId;

    /// <inheritdoc />
    public string ModelId => _options.ModelId;

    /// <inheritdoc />
    public string ModelFamily => _options.ModelFamily;

    /// <inheritdoc />
    public async Task<Ballot> JudgeAsync(
        Candidate candidate,
        Rubric rubric,
        JudgeContext context,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(rubric);
        ArgumentNullException.ThrowIfNull(context);

        var prompt = _options.PromptRenderer(candidate, rubric, context);
        var reply = await _transport(prompt, cancellationToken).ConfigureAwait(false);
        var ballot = JudgeReplyParser.Parse(reply, rubric, _options.JudgeId, _options.ModelId, _options.ModelFamily);

        if (ballot.Abstained)
        {
            _logger?.LogWarning(
                "Judge {JudgeId} abstained on candidate {CandidateId}: {AbstainReason}.",
                _options.JudgeId,
                candidate.CandidateId,
                ballot.AbstainReason
            );
        }

        return ballot;
    }
}
