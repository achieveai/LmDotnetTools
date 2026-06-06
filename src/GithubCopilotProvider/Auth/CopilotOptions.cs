namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;

/// <summary>
///     Configuration for routing provider requests through the GitHub Copilot API
///     (<c>https://api.enterprise.githubcopilot.com</c>). Controls the host plus the static
///     headers the Copilot backend expects on every model call.
/// </summary>
/// <remarks>
///     Defaults mimic the GitHub Copilot CLI so the backend treats requests as coming from a
///     known integration. Callers that have their own approved integration id should override
///     <see cref="IntegrationId"/>, <see cref="EditorVersion"/>, and <see cref="UserAgent"/>.
/// </remarks>
public sealed record CopilotOptions
{
    /// <summary>Default GitHub Copilot enterprise host (no trailing path).</summary>
    public const string DefaultBaseUrl = "https://api.enterprise.githubcopilot.com";

    /// <summary>The Copilot API host root, e.g. <c>https://api.enterprise.githubcopilot.com</c>.</summary>
    public string BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary><c>copilot-integration-id</c> header value. Identifies the calling integration.</summary>
    public string IntegrationId { get; init; } = "copilot-developer-cli";

    /// <summary><c>editor-version</c> header value.</summary>
    public string EditorVersion { get; init; } = "copilot/1.0.57-3";

    /// <summary><c>user-agent</c> header value.</summary>
    public string UserAgent { get; init; } = "copilot/1.0.57-3 (client/github/cli win32 v24.16.0) term/unknown";

    /// <summary><c>x-github-api-version</c> header value.</summary>
    public string GitHubApiVersion { get; init; } = "2026-06-01";

    /// <summary>
    ///     Default <c>x-initiator</c> value (<c>user</c> or <c>agent</c>). Marks whether the
    ///     originating action was user- or agent-initiated.
    /// </summary>
    public string DefaultInitiator { get; init; } = "user";

    /// <summary>
    ///     Optional default <c>x-interaction-type</c> value
    ///     (<c>conversation-user</c> / <c>conversation-agent</c> / <c>conversation-background</c>).
    ///     Omitted when null.
    /// </summary>
    public string? DefaultInteractionType { get; init; }

    /// <summary>
    ///     Static per-client headers added to every request (after the standard Copilot headers).
    ///     Used for protocol-specific headers such as <c>anthropic-version</c> (Messages API) or
    ///     <c>openai-intent</c> (Responses API). Existing headers on a request are not overwritten.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }
}
