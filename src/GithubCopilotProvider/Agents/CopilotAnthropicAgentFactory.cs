using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Http;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Performance;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;

/// <summary>
///     Builds an <see cref="AnthropicAgent"/> that talks the Anthropic Messages API through the
///     GitHub Copilot backend (<c>POST {host}/v1/messages</c>) instead of api.anthropic.com.
/// </summary>
/// <remarks>
///     The agent, client, request builder, and SSE parsing are reused unchanged — only the transport
///     differs: a <see cref="CopilotHeadersHandler"/> supplies the bearer token and Copilot headers,
///     and the base URL points at the Copilot host. The required <c>anthropic-version</c> header is
///     injected via <see cref="CopilotOptions.ExtraHeaders"/>.
/// </remarks>
public static class CopilotAnthropicAgentFactory
{
    private const string AnthropicVersion = "2023-06-01";

    /// <summary>
    ///     Creates an <see cref="AnthropicAgent"/> routed through GitHub Copilot.
    /// </summary>
    /// <param name="name">Agent name.</param>
    /// <param name="tokenProvider">Source of the GitHub OAuth bearer token.</param>
    /// <param name="session">Optional shared tracking ids; a new context is created when omitted.</param>
    /// <param name="options">Optional Copilot header options; defaults target the enterprise host.</param>
    /// <param name="performanceTracker">Optional performance tracker.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="retryOptions">Optional retry configuration.</param>
    public static AnthropicAgent Create(
        string name,
        ICopilotTokenProvider tokenProvider,
        CopilotSessionContext? session = null,
        CopilotOptions? options = null,
        IPerformanceTracker? performanceTracker = null,
        ILogger<AnthropicAgent>? logger = null,
        RetryOptions? retryOptions = null
    )
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        var baseOptions = options ?? new CopilotOptions();
        var copilotOptions = baseOptions with
        {
            DefaultInteractionType = baseOptions.DefaultInteractionType ?? "conversation-user",
            ExtraHeaders = WithAnthropicVersion(baseOptions.ExtraHeaders),
        };

        var context = session ?? new CopilotSessionContext();
        var host = copilotOptions.BaseUrl.TrimEnd('/');

        var httpClient = CopilotHttpClientFactory.Create(host, tokenProvider, context, copilotOptions);

        // The client appends "/messages", so the base URL must carry the "/v1" prefix to hit
        // {host}/v1/messages.
        var client = new AnthropicClient(httpClient, $"{host}/v1", performanceTracker, logger, retryOptions);

        return new AnthropicAgent(name, client, logger);
    }

    private static IReadOnlyDictionary<string, string> WithAnthropicVersion(
        IReadOnlyDictionary<string, string>? existing
    )
    {
        var merged = existing is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);

        if (!merged.ContainsKey("anthropic-version"))
        {
            merged["anthropic-version"] = AnthropicVersion;
        }

        return merged;
    }
}
