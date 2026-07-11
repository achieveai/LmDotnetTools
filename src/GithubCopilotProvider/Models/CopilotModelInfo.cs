using System.Collections.Immutable;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;

/// <summary>
///     The request transport a Copilot model is reachable through, derived from the model's
///     <c>supported_endpoints</c>. Only <see cref="Anthropic"/> and <see cref="Responses"/> are
///     routable by the sample today; everything else (e.g. <c>/chat/completions</c>-only models
///     such as Gemini) is <see cref="Unsupported"/> and filtered out.
/// </summary>
public enum CopilotModelTransport
{
    /// <summary>No routable endpoint (e.g. only <c>/chat/completions</c>).</summary>
    Unsupported = 0,

    /// <summary>Reachable via <c>POST /v1/messages</c> (Anthropic Messages shape).</summary>
    Anthropic = 1,

    /// <summary>Reachable via <c>POST /responses</c> (OpenAI Responses shape).</summary>
    Responses = 2,
}

/// <summary>
///     A single GitHub Copilot model, projected from the Copilot <c>GET /models</c> response down to
///     the fields the sample needs to list and route it.
/// </summary>
/// <param name="Id">Raw Copilot model id (e.g. <c>claude-opus-4.8</c>, <c>gpt-5.5</c>). Used verbatim as the request model id.</param>
/// <param name="DisplayName">Human-friendly label from the response <c>name</c> (falls back to <see cref="Id"/>).</param>
/// <param name="Vendor">Normalized publisher — <c>Anthropic</c> or <c>OpenAI</c> (<c>Azure OpenAI</c> collapses to <c>OpenAI</c>).</param>
/// <param name="Transport">The routable transport derived from <c>supported_endpoints</c>.</param>
/// <param name="SupportsAdaptiveThinking">
///     <c>true</c> when the model advertises <c>capabilities.supports.adaptive_thinking</c>. Such
///     models reject the classic <c>thinking.type.enabled</c> budget request (they require
///     <c>thinking.type.adaptive</c> + <c>output_config.effort</c>), so the sample must not send the
///     classic thinking parameter to them.
/// </param>
public sealed record CopilotModelInfo(
    string Id,
    string DisplayName,
    CopilotModelVendor Vendor,
    CopilotModelTransport Transport,
    bool SupportsAdaptiveThinking = false
)
{
    private ImmutableArray<string> _reasoningEfforts = [];

    /// <summary>
    ///     Reasoning effort values advertised by <c>capabilities.supports.reasoning_effort</c>.
    /// </summary>
    public IReadOnlyList<string> ReasoningEfforts
    {
        get => _reasoningEfforts;
        init => _reasoningEfforts = [.. value];
    }

    /// <inheritdoc />
    /// <remarks>
    /// Equality intentionally covers the legacy positional contract only. The newly projected
    /// reasoning-effort capability list is excluded to preserve existing equality and hash behavior.
    /// </remarks>
    public bool Equals(CopilotModelInfo? other)
    {
        return ReferenceEquals(this, other)
            || (
                other is not null
                && Id == other.Id
                && DisplayName == other.DisplayName
                && Vendor == other.Vendor
                && Transport == other.Transport
                && SupportsAdaptiveThinking == other.SupportsAdaptiveThinking
            );
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(DisplayName);
        hash.Add(Vendor);
        hash.Add(Transport);
        hash.Add(SupportsAdaptiveThinking);
        return hash.ToHashCode();
    }
}

/// <summary>
///     The normalized publisher partition a Copilot model belongs to. The sample only surfaces these
///     two vendors; Google/Microsoft and any other publisher are excluded during parsing.
/// </summary>
public enum CopilotModelVendor
{
    /// <summary>Anthropic (Claude) models.</summary>
    Anthropic = 1,

    /// <summary>OpenAI (GPT/o-series) models, including Copilot's <c>Azure OpenAI</c>-hosted variants.</summary>
    OpenAI = 2,
}
