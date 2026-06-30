using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.Misc.Utils;

/// <summary>
///     Backend-agnostic <c>WebSearch</c> tool. Gates on the presence of an API key, validates the
///     query, delegates to an <see cref="IWebSearchProvider" />, then frames, sanitizes, and bounds
///     the Markdown it returns to the model. Provider exceptions are mapped to bounded, sanitized
///     error strings so that raw upstream bodies, exception messages, or the API key are never
///     surfaced.
/// </summary>
public sealed class WebSearchTool
{
    /// <summary>
    ///     The tool name advertised to the model and used for registration.
    /// </summary>
    public const string ToolName = "WebSearch";

    private readonly IWebSearchProvider _provider;
    private readonly WebToolsOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    ///     Initializes a new <see cref="WebSearchTool" />.
    /// </summary>
    /// <param name="provider">The backend used to run searches.</param>
    /// <param name="options">Web tools configuration (API key, output cap, query length).</param>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger{T}.Instance" />.</param>
    public WebSearchTool(IWebSearchProvider provider, WebToolsOptions options, ILogger? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<WebSearchTool>.Instance;
        Contract = BuildContract();
    }

    /// <summary>
    ///     The function contract describing this tool to the model.
    /// </summary>
    public FunctionContract Contract { get; }

    /// <summary>
    ///     The tool handler delegate that threads the cancellation token through to the provider.
    /// </summary>
    public ToolHandler Handler => HandleAsync;

    private static FunctionContract BuildContract()
    {
        return new FunctionContract
        {
            Name = ToolName,
            Description = "Run a web search and return the ranked results as Markdown.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "query",
                    Description = "The search query.",
                    ParameterType = JsonSchemaObject.String("The search query."),
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "count",
                    Description = "Optional maximum number of results to return.",
                    ParameterType = JsonSchemaObject.Integer("Optional maximum number of results to return."),
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "country",
                    Description = "Optional ISO country code used to localize results (for example, \"US\").",
                    ParameterType = JsonSchemaObject.String(
                        "Optional ISO country code used to localize results (for example, \"US\")."
                    ),
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "language",
                    Description = "Optional language code used to localize results (for example, \"en\").",
                    ParameterType = JsonSchemaObject.String(
                        "Optional language code used to localize results (for example, \"en\")."
                    ),
                    IsRequired = false,
                },
            ],
            ReturnType = typeof(string),
        };
    }

    private async Task<ToolHandlerResult> HandleAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        // Up-front key gate: WebSearch requires an API key, so fail fast without touching the provider.
        if (string.IsNullOrWhiteSpace(_options.JinaApiKey))
        {
            return ToolHandlerResult.FromError("WebSearch unavailable: set JINA_API_KEY.");
        }

        string? query;
        int? count;
        string? country;
        string? language;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var root = document.RootElement;
            query = WebToolArgs.ReadString(root, "query");
            count = WebToolArgs.ReadInt(root, "count");
            country = WebToolArgs.ReadString(root, "country");
            language = WebToolArgs.ReadString(root, "language");
        }
        catch (JsonException)
        {
            return Error("WebSearch error: arguments were not valid JSON.");
        }

        var validation = WebInputValidator.ValidateQuery(query, _options.MaxQueryLength);
        if (!validation.IsValid)
        {
            return Error("WebSearch error: " + validation.Error);
        }

        try
        {
            var result = await _provider.SearchAsync(
                validation.Value!,
                new WebSearchOptions
                {
                    Count = count,
                    Country = country,
                    Language = language,
                },
                cancellationToken
            );

            if (result.Items.Count == 0)
            {
                return ToolHandlerResult.FromText("No results found.");
            }

            var markdown = WebToolOutput.FormatSearch(result);
            // Use a generic source label: the query may carry PII/secrets and must not be echoed back.
            var framed = WebToolOutput.WrapUntrusted(markdown, "web search");
            var sanitized = WebToolOutput.Sanitize(framed, _options.JinaApiKey);
            var bounded = WebToolOutput.Truncate(sanitized, _options.OutputCap);
            return ToolHandlerResult.FromText(bounded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-initiated cancellation propagates so the agent loop can unwind.
            throw;
        }
        catch (OperationCanceledException)
        {
            // The linked per-call timeout fired without the caller cancelling.
            return Error("WebSearch error: request timed out.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return Error("WebSearch error: the JINA_API_KEY is invalid (401).");
        }
        catch (HttpRequestException ex)
        {
            return Error(MapHttpError(ex));
        }
        catch (Exception)
        {
            return Error("WebSearch error: could not run the search.");
        }
    }

    /// <summary>
    ///     Maps an HTTP failure to a bounded message. The status code is the only upstream detail
    ///     surfaced; the exception message (which may carry the raw response body) is never used.
    /// </summary>
    private static string MapHttpError(HttpRequestException ex)
    {
        return ex.StatusCode == HttpStatusCode.NotFound
            ? "WebSearch error: page not found (404)."
            : $"WebSearch error: upstream request failed ({(int?)ex.StatusCode}).";
    }

    /// <summary>
    ///     Builds an error result, redacting the API key as a defense-in-depth measure and logging a
    ///     sanitized reason (never the raw exception, body, or key).
    /// </summary>
    private ToolHandlerResult Error(string message)
    {
        var sanitized = WebToolOutput.Sanitize(message, _options.JinaApiKey);
        _logger.LogWarning("WebSearch returned an error to the model: {Reason}", sanitized);
        return ToolHandlerResult.FromError(sanitized);
    }
}
