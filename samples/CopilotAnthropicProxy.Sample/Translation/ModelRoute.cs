using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;

/// <summary>The API dialect a request arrived in.</summary>
public enum ProxyDialect
{
    /// <summary>Anthropic Messages — Claude Code.</summary>
    AnthropicMessages,

    /// <summary>OpenAI Chat Completions — opencode and most OpenAI SDKs.</summary>
    ChatCompletions,

    /// <summary>OpenAI Responses — Codex CLI.</summary>
    Responses,
}

/// <summary>How a request must be served.</summary>
public enum ProxyRouteKind
{
    /// <summary>Forward the body to Copilot essentially unchanged.</summary>
    Passthrough,

    /// <summary>Rewrite an Anthropic Messages request into an OpenAI Responses request, and the reply back.</summary>
    TranslateAnthropicToResponses,
}

/// <summary>The resolved upstream target for one request.</summary>
public sealed record ModelRoute(ProxyRouteKind Kind, string UpstreamPath, ProxyModelInfo Model);

/// <summary>
///     The routing table. Copilot serves three transports and every model advertises which of them it
///     honors, so the only question is whether the client's dialect matches one the model accepts.
///     Exactly one mismatch is worth translating: Anthropic Messages in, for a model that only speaks
///     Responses. Everything else either passes through or 404s.
/// </summary>
public static class ModelRouter
{
    /// <summary>Copilot's Anthropic Messages path.</summary>
    public const string MessagesPath = CopilotModelsResponse.MessagesEndpoint;

    /// <summary>Copilot's Anthropic token-counting path.</summary>
    public const string CountTokensPath = "/v1/messages/count_tokens";

    /// <summary>Copilot's OpenAI Chat Completions path.</summary>
    public const string ChatCompletionsPath = ProxyModelResolver.ChatCompletionsEndpoint;

    /// <summary>Copilot's OpenAI Responses path.</summary>
    public const string ResponsesPath = CopilotModelsResponse.ResponsesEndpoint;

    /// <summary>
    ///     Resolves how to serve <paramref name="model"/> for an inbound <paramref name="dialect"/>.
    ///     Returns null when the model cannot serve that dialect at all; the caller answers 404.
    ///
    ///     A model with NO endpoint metadata (pinned via COPILOT_ANTHROPIC_MODEL, or discovered from a
    ///     legacy /models shape) is treated as Anthropic-Messages-capable and nothing else, which is
    ///     precisely how this proxy behaved before endpoint metadata existed.
    /// </summary>
    public static ModelRoute? Resolve(ProxyDialect dialect, ProxyModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var noMetadata = model.Endpoints.Count == 0;

        return dialect switch
        {
            ProxyDialect.AnthropicMessages when noMetadata || model.Supports(MessagesPath) => new ModelRoute(
                ProxyRouteKind.Passthrough,
                MessagesPath,
                model
            ),
            ProxyDialect.AnthropicMessages when model.Supports(ResponsesPath) => new ModelRoute(
                ProxyRouteKind.TranslateAnthropicToResponses,
                ResponsesPath,
                model
            ),
            ProxyDialect.ChatCompletions when noMetadata || model.Supports(ChatCompletionsPath) => new ModelRoute(
                ProxyRouteKind.Passthrough,
                ChatCompletionsPath,
                model
            ),
            ProxyDialect.Responses when model.Supports(ResponsesPath) => new ModelRoute(
                ProxyRouteKind.Passthrough,
                ResponsesPath,
                model
            ),
            _ => null,
        };
    }

    /// <summary>
    ///     The ids that can serve <paramref name="dialect"/>, in catalog order. Used to make a 404 body
    ///     actionable — telling a client its model is unavailable is only useful alongside the list of
    ///     models that are.
    /// </summary>
    public static IReadOnlyList<string> Servable(ProxyDialect dialect, ProxyModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return [.. catalog.Models.Where(m => Resolve(dialect, m) is not null).Select(m => m.Id)];
    }
}
