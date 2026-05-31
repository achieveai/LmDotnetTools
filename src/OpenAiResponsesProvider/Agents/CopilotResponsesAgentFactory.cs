using AchieveAi.LmDotnetTools.LmCore.Auth;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;

/// <summary>Transport choice for the GitHub Copilot Responses API.</summary>
public enum CopilotResponsesTransport
{
    /// <summary>HTTP <c>POST /responses</c> with Server-Sent Events streaming.</summary>
    Sse,

    /// <summary>Persistent <c>GET /responses</c> WebSocket (the Copilot CLI's main conversation transport).</summary>
    WebSocket,
}

/// <summary>
///     Builds an <see cref="OpenAiResponsesAgent"/> that talks the OpenAI Responses API through the
///     GitHub Copilot backend (<c>{host}/responses</c>) over either SSE or WebSocket. The agent and
///     event→message mapping are reused unchanged; only the transport client differs.
/// </summary>
public static class CopilotResponsesAgentFactory
{
    private const string ResponsesPath = "/responses";

    /// <summary>Creates an <see cref="OpenAiResponsesAgent"/> routed through GitHub Copilot.</summary>
    /// <param name="name">Agent name.</param>
    /// <param name="tokenProvider">Source of the GitHub OAuth bearer token.</param>
    /// <param name="transport">SSE or WebSocket. Defaults to WebSocket (the CLI's primary transport).</param>
    /// <param name="session">Optional shared tracking ids; a new context is created when omitted.</param>
    /// <param name="options">Optional Copilot header options.</param>
    /// <param name="logger">Optional logger.</param>
    public static OpenAiResponsesAgent Create(
        string name,
        ICopilotTokenProvider tokenProvider,
        CopilotResponsesTransport transport = CopilotResponsesTransport.WebSocket,
        CopilotSessionContext? session = null,
        CopilotOptions? options = null,
        ILogger<OpenAiResponsesAgent>? logger = null
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

        var client = transport == CopilotResponsesTransport.Sse
            ? CreateSseClient(host, tokenProvider, context, copilotOptions)
            : CreateWebSocketClient(host, tokenProvider, context, copilotOptions, logger);

        return new OpenAiResponsesAgent(name, client, logger);
    }

    private static IOpenAiResponsesClient CreateSseClient(
        string host,
        ICopilotTokenProvider tokenProvider,
        CopilotSessionContext context,
        CopilotOptions options
    )
    {
        var httpClient = CopilotHttpClientFactory.Create(host, tokenProvider, context, options);
        return new OpenAiResponsesClient(httpClient, disposeClient: true, logger: null, responsesPath: ResponsesPath);
    }

    private static IOpenAiResponsesClient CreateWebSocketClient(
        string host,
        ICopilotTokenProvider tokenProvider,
        CopilotSessionContext context,
        CopilotOptions options,
        ILogger? logger
    )
    {
        var wsEndpoint = new Uri($"{ToWebSocketScheme(host)}{ResponsesPath}");
        return new CopilotResponsesWebSocketClient(wsEndpoint, tokenProvider, context, options, logger);
    }

    private static string ToWebSocketScheme(string host)
    {
        if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat("wss://", host.AsSpan("https://".Length));
        }

        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat("ws://", host.AsSpan("http://".Length));
        }

        return host;
    }

    private static IReadOnlyDictionary<string, string> WithOpenAiIntent(
        IReadOnlyDictionary<string, string>? existing
    )
    {
        var merged = existing is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);

        if (!merged.ContainsKey("openai-intent"))
        {
            merged["openai-intent"] = "conversation-agent";
        }

        return merged;
    }
}
