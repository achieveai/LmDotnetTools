using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;

/// <summary>
///     Discovers the GitHub Copilot model catalog by calling <c>GET /models</c> through the Copilot
///     backend and projecting the response to the routable Anthropic/OpenAI models via
///     <see cref="CopilotModelCatalogParser"/>.
/// </summary>
/// <remarks>
///     Failures (no token, network error, non-success status, malformed body) are swallowed and
///     surfaced as an empty list so a discovery outage never takes down the caller — the caller
///     decides how to degrade (e.g. fall back to a built-in set).
/// </remarks>
public sealed class CopilotModelsClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    /// <summary>
    ///     Creates a client that builds its own Copilot-authenticated <see cref="HttpClient"/> from the
    ///     supplied token provider, session, and options.
    /// </summary>
    public CopilotModelsClient(
        ICopilotTokenProvider tokenProvider,
        CopilotSessionContext? session = null,
        CopilotOptions? options = null,
        ILogger? logger = null)
        : this(BuildHttpClient(tokenProvider, session, options), logger)
    {
    }

    /// <summary>
    ///     Creates a client over a caller-supplied <see cref="HttpClient"/> (must target the Copilot host
    ///     root). Primarily a test seam.
    /// </summary>
    public CopilotModelsClient(HttpClient httpClient, ILogger? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
    }

    /// <summary>
    ///     Fetches and parses the routable Anthropic/OpenAI Copilot models. Returns an empty list on any
    ///     failure.
    /// </summary>
    public async Task<IReadOnlyList<CopilotModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/models", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "Copilot model discovery returned {StatusCode}; treating catalog as empty.",
                    (int)response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var models = CopilotModelCatalogParser.Parse(json);
            _logger?.LogInformation("Discovered {Count} routable Copilot models.", models.Count);
            return models;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _logger?.LogWarning(ex, "Copilot model discovery failed; treating catalog as empty.");
            return [];
        }
    }

    private static HttpClient BuildHttpClient(
        ICopilotTokenProvider tokenProvider,
        CopilotSessionContext? session,
        CopilotOptions? options)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        var effectiveOptions = options ?? new CopilotOptions();
        return CopilotHttpClientFactory.Create(
            effectiveOptions.BaseUrl,
            tokenProvider,
            session ?? new CopilotSessionContext(),
            effectiveOptions);
    }
}
