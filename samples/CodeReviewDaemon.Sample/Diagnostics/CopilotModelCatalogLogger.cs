using System.Text.Json;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;

namespace CodeReviewDaemon.Sample.Diagnostics;

/// <summary>
/// On daemon startup, discovers and logs the GitHub Copilot model catalog the daemon's own Copilot
/// credential (<see cref="CliCredentialCopilotTokenProvider"/> — the same one the review agent uses) can
/// see. Two views are logged from a single <c>GET /models</c> call:
/// <list type="bullet">
///   <item><b>Raw catalog</b> — every model Copilot returns, including non-routable
///   (<c>/chat/completions</c>-only) ones, with its id, vendor, and which supported endpoints it exposes.</item>
///   <item><b>Routable subset</b> — the Anthropic/OpenAI models the review agent can actually use as a
///   <c>ReviewModelId</c> (Anthropic via <c>/v1/messages</c>, OpenAI via <c>/responses</c>), with the
///   transport it routes through. This is the list to pick a valid model id from.</item>
/// </list>
/// Runs once shortly after startup, best-effort and bounded: it executes on a background task (a
/// <see cref="BackgroundService"/>, so its <c>GET /models</c> call — up to <see cref="DiscoveryTimeout"/>
/// — never delays host startup), and any failure (no credential, network, non-success, malformed body) is
/// logged and swallowed so model discovery never blocks or crashes boot. Purely diagnostic — it makes
/// no routing or config decisions.
/// </summary>
internal sealed class CopilotModelCatalogLogger : BackgroundService
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<CopilotModelCatalogLogger> _logger;

    public CopilotModelCatalogLogger(ILogger<CopilotModelCatalogLogger> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(DiscoveryTimeout);
            await LogCatalogAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Copilot model catalog discovery failed at startup; continuing without it.");
        }
    }

    private async Task LogCatalogAsync(CancellationToken cancellationToken)
    {
        var tokenProvider = new CliCredentialCopilotTokenProvider();
        var session = new CopilotSessionContext();
        var options = new CopilotOptions();

        using var http = CopilotHttpClientFactory.Create(options.BaseUrl, tokenProvider, session, options);
        using var response = await http.GetAsync("/models", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Copilot model discovery GET {BaseUrl}/models returned {StatusCode}; no catalog to log.",
                options.BaseUrl,
                (int)response.StatusCode);
            return;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        // ── Raw catalog: every model Copilot returns, unfiltered ────────────────────────────────────
        var raw = new List<(string Id, string Vendor, string Endpoints, string Name)>();
        foreach (var item in CopilotModelsResponse.EnumerateModelEntries(document.RootElement))
        {
            var id = CopilotModelsResponse.GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var messages = CopilotModelsResponse.SupportsEndpoint(item, CopilotModelsResponse.MessagesEndpoint);
            var responses =
                CopilotModelsResponse.SupportsEndpoint(item, CopilotModelsResponse.ResponsesEndpoint)
                || CopilotModelsResponse.SupportsEndpoint(item, CopilotModelsResponse.ResponsesWebSocketEndpoint);
            var endpoints = (messages, responses) switch
            {
                (true, true) => "messages+responses",
                (true, false) => "messages",
                (false, true) => "responses",
                _ => "other/chat-only",
            };

            raw.Add((
                id,
                CopilotModelsResponse.GetString(item, "vendor") ?? "?",
                endpoints,
                CopilotModelsResponse.GetString(item, "name") ?? id));
        }

        _logger.LogInformation(
            "Copilot model catalog: {Count} model(s) visible to the daemon credential at {BaseUrl}.",
            raw.Count,
            options.BaseUrl);
        foreach (var m in raw.OrderBy(m => m.Vendor, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "  Copilot model: id={Id} | vendor={Vendor} | endpoints={Endpoints} | name=\"{Name}\"",
                m.Id,
                m.Vendor,
                m.Endpoints,
                m.Name);
        }

        // ── Routable subset: models usable as a ReviewModelId (pick from these) ─────────────────────
        var routable = CopilotModelCatalogParser.Parse(json);
        _logger.LogInformation(
            "Copilot routable models (usable as CodeReviewDaemon:ReviewModelId): {Count}.",
            routable.Count);
        foreach (var m in routable.OrderBy(m => m.Vendor).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "  Routable: id={Id} | vendor={Vendor} | transport={Transport} | adaptiveThinking={Adaptive} | name=\"{Name}\"",
                m.Id,
                m.Vendor,
                m.Transport,
                m.SupportsAdaptiveThinking,
                m.DisplayName);
        }
    }
}
