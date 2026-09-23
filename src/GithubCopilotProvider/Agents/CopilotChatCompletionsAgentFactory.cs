using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Http;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;

/// <summary>
///     Builds an <see cref="OpenClientAgent"/> that talks OpenAI Chat Completions through the GitHub
///     Copilot backend (<c>POST {host}/chat/completions</c>) — the only endpoint Copilot exposes for
///     Gemini models.
/// </summary>
/// <remarks>
///     The agent and client are reused unchanged. Copilot's dialect differences (zero-usage chunks,
///     <c>reasoning_text</c>/<c>reasoning_opaque</c>) are translated by
///     <see cref="CopilotChatCompletionsDialectHandler"/>, which sits in this client's pipeline only.
/// </remarks>
public static class CopilotChatCompletionsAgentFactory
{
    /// <summary>Creates an <see cref="OpenClientAgent"/> routed through GitHub Copilot.</summary>
    /// <param name="name">Agent name.</param>
    /// <param name="tokenProvider">Source of the GitHub OAuth bearer token.</param>
    /// <param name="timeout">
    ///     HTTP timeout (<c>null</c> = the shared 5-minute default). Streaming reads with
    ///     <c>ResponseHeadersRead</c>, so this bounds time-to-first-response, not stream length.
    /// </param>
    /// <param name="session">Optional shared tracking ids; a new context is created when omitted.</param>
    /// <param name="options">Optional Copilot header options; defaults target the enterprise host.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="retryOptions">Optional retry configuration.</param>
    public static OpenClientAgent Create(
        string name,
        ICopilotTokenProvider tokenProvider,
        TimeSpan? timeout = null,
        CopilotSessionContext? session = null,
        CopilotOptions? options = null,
        ILogger<OpenClientAgent>? logger = null,
        RetryOptions? retryOptions = null
    ) => Create(name, tokenProvider, timeout, session, options, logger, retryOptions, innerHandler: null);

    // internal so tests can drive the real pipeline (headers + dialect handler + OpenClient) over a
    // recorded transport. The public overload always uses the default transport.
    internal static OpenClientAgent Create(
        string name,
        ICopilotTokenProvider tokenProvider,
        TimeSpan? timeout,
        CopilotSessionContext? session,
        CopilotOptions? options,
        ILogger<OpenClientAgent>? logger,
        RetryOptions? retryOptions,
        HttpMessageHandler? innerHandler
    )
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        var baseOptions = options ?? new CopilotOptions();
        var copilotOptions = baseOptions with
        {
            DefaultInteractionType = baseOptions.DefaultInteractionType ?? "conversation-user",
            ExtraHeaders = WithOpenAiIntent(baseOptions.ExtraHeaders),
        };

        var context = session ?? new CopilotSessionContext();
        var host = copilotOptions.BaseUrl.TrimEnd('/');

        var httpClient = CopilotHttpClientFactory.Create(
            host,
            tokenProvider,
            context,
            copilotOptions,
            timeout,
            new CopilotChatCompletionsDialectHandler(innerHandler)
        );

        // The client owns the HttpClient built above, so disposing the agent releases its handlers.
        var client = new OpenClient(
            httpClient,
            host,
            logger: logger,
            retryOptions: retryOptions,
            disposeHttpClient: true
        );
        return new OpenClientAgent(name, client, logger);
    }

    private static IReadOnlyDictionary<string, string> WithOpenAiIntent(IReadOnlyDictionary<string, string>? existing)
    {
        var merged = existing is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);

        _ = merged.TryAdd("openai-intent", "conversation-agent");
        return merged;
    }
}
