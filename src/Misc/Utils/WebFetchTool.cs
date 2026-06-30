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
///     Backend-agnostic <c>WebFetch</c> tool. Validates the URL, delegates to an
///     <see cref="IWebFetchProvider" />, then frames, sanitizes, and bounds the Markdown it returns
///     to the model. Provider exceptions are mapped to bounded, sanitized error strings so that raw
///     upstream bodies, exception messages, or the API key are never surfaced.
/// </summary>
public sealed class WebFetchTool
{
    /// <summary>
    ///     The tool name advertised to the model and used for registration.
    /// </summary>
    public const string ToolName = "WebFetch";

    private readonly IWebFetchProvider _provider;
    private readonly WebToolsOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    ///     Initializes a new <see cref="WebFetchTool" />.
    /// </summary>
    /// <param name="provider">The backend used to fetch pages.</param>
    /// <param name="options">Web tools configuration (API key, output cap).</param>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger{T}.Instance" />.</param>
    public WebFetchTool(IWebFetchProvider provider, WebToolsOptions options, ILogger? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<WebFetchTool>.Instance;
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
            Description = "Fetch a single web page and return its content as Markdown.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "url",
                    Description = "The absolute http/https URL of the page to fetch.",
                    ParameterType = JsonSchemaObject.String("The absolute http/https URL of the page to fetch."),
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "targetSelector",
                    Description = "Optional CSS selector identifying the region of the page to extract.",
                    ParameterType = JsonSchemaObject.String(
                        "Optional CSS selector identifying the region of the page to extract."
                    ),
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "noCache",
                    Description = "When true, bypasses any cached copy and fetches fresh content.",
                    ParameterType = JsonSchemaObject.Boolean(
                        "When true, bypasses any cached copy and fetches fresh content."
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
        string? url;
        string? targetSelector;
        bool? noCache;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var root = document.RootElement;
            url = WebToolArgs.ReadString(root, "url");
            targetSelector = WebToolArgs.ReadString(root, "targetSelector");
            noCache = WebToolArgs.ReadBool(root, "noCache");
        }
        catch (JsonException)
        {
            return Error("WebFetch error: arguments were not valid JSON.");
        }

        var validation = WebInputValidator.ValidateUrl(url);
        if (!validation.IsValid)
        {
            return Error("WebFetch error: " + validation.Error);
        }

        try
        {
            // targetSelector is LLM-controlled and is later added to the X-Target-Selector header via
            // TryAddWithoutValidation, so validate it at this trust boundary: a control character (CR/LF)
            // or an overlong value could enable header injection. Omit it rather than failing the call.
            var result = await _provider.FetchAsync(
                validation.Value!,
                new WebFetchOptions { TargetSelector = NormalizeTargetSelector(targetSelector), NoCache = noCache },
                cancellationToken
            );

            var markdown = WebToolOutput.FormatFetch(result);
            // Minimize the URL for display: the full URL is still fetched, but the source label drops the
            // query, fragment, and userinfo so secrets/PII in those parts are never echoed to the model.
            var framed = WebToolOutput.WrapUntrusted(markdown, WebToolOutput.MinimizeUrl(validation.Value));
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
            return Error("WebFetch error: request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return Error(MapHttpError(ex));
        }
        catch (Exception)
        {
            return Error("WebFetch error: could not fetch the page.");
        }
    }

    /// <summary>
    ///     The maximum length accepted for <c>targetSelector</c>; longer values are omitted.
    /// </summary>
    private const int MaxTargetSelectorLength = 256;

    /// <summary>
    ///     Accepts a <c>targetSelector</c> only when it carries no control characters (including CR/LF,
    ///     which could enable header injection) and stays within <see cref="MaxTargetSelectorLength" />;
    ///     otherwise omits it (<c>null</c>) so no <c>X-Target-Selector</c> header is sent.
    /// </summary>
    private static string? NormalizeTargetSelector(string? selector) =>
        selector is not null && selector.Length <= MaxTargetSelectorLength && !selector.Any(char.IsControl)
            ? selector
            : null;

    /// <summary>
    ///     Maps an HTTP failure to a bounded message. The status code is the only upstream detail
    ///     surfaced; the exception message (which may carry the raw response body) is never used.
    /// </summary>
    private static string MapHttpError(HttpRequestException ex)
    {
        return ex.StatusCode == HttpStatusCode.NotFound
            ? "WebFetch error: page not found (404)."
            : $"WebFetch error: upstream request failed ({(int?)ex.StatusCode}).";
    }

    /// <summary>
    ///     Builds an error result, redacting the API key as a defense-in-depth measure and logging a
    ///     sanitized reason (never the raw exception, body, or key).
    /// </summary>
    private ToolHandlerResult Error(string message)
    {
        var sanitized = WebToolOutput.Sanitize(message, _options.JinaApiKey);
        _logger.LogWarning("WebFetch returned an error to the model: {Reason}", sanitized);
        return ToolHandlerResult.FromError(sanitized);
    }
}
