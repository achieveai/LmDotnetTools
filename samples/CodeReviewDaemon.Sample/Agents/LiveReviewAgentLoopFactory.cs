using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The production <see cref="IReviewAgentLoopFactory"/>: assembles an OpenAI-compatible
/// <see cref="MultiTurnAgentLoop"/> from a profile (mirrors the wiring in <c>LmStreaming.Sample</c>).
/// The API key and base URL come from <c>OPENAI_API_KEY</c> / <c>OPENAI_BASE_URL</c> (no config knob).
/// The registry is empty — the daemon's agents are collect-only text agents that reason over the diff
/// the executor supplies and never call tools.
/// <para>
/// This path is <b>dead by default</b>: with the repo allow-list empty the poller has no targets, so
/// the executor is never invoked and this factory is never called. It does no work at construction
/// (lazy per run), so registering it cannot affect daemon boot or the route surface.
/// </para>
/// <para>
/// <b>Lifetime.</b> The provider <see cref="HttpClient"/> is created once on first use and shared across
/// every loop (the standard long-lived-<c>HttpClient</c> pattern), then disposed with this singleton —
/// so the per-run loops the executor <c>await using</c>s never each leak a client.
/// </para>
/// </summary>
internal sealed class LiveReviewAgentLoopFactory : IReviewAgentLoopFactory, IDisposable
{
    private const string DefaultBaseUrl = "https://api.openai.com/v1";

    private readonly ILoggerFactory _loggerFactory;
    private readonly object _httpClientGate = new();
    private HttpClient? _httpClient;
    private string? _baseUrl;

    public LiveReviewAgentLoopFactory(ILoggerFactory loggerFactory) =>
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    public IMultiTurnAgent Create(AgentProfile profile, string? modelId, string threadId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var (httpClient, baseUrl) = GetSharedHttpClient();
        var client = new OpenClient(httpClient, baseUrl, logger: _loggerFactory.CreateLogger<OpenClient>());
        var providerAgent = new OpenClientAgent("OpenAI", client, _loggerFactory.CreateLogger<OpenClientAgent>());

        return new MultiTurnAgentLoop(
            providerAgent,
            new FunctionRegistry(),
            threadId,
            systemPrompt: profile.SystemPrompt,
            defaultOptions: new GenerateReplyOptions { ModelId = modelId ?? string.Empty },
            logger: _loggerFactory.CreateLogger<MultiTurnAgentLoop>());
    }

    private (HttpClient Client, string BaseUrl) GetSharedHttpClient()
    {
        if (_httpClient is not null)
        {
            return (_httpClient, _baseUrl!);
        }

        lock (_httpClientGate)
        {
            if (_httpClient is null)
            {
                var apiKey = EnvironmentVariableHelper.GetApiKeyFromEnv("OPENAI_API_KEY");
                _baseUrl = EnvironmentVariableHelper.GetApiBaseUrlFromEnv("OPENAI_BASE_URL", defaultValue: DefaultBaseUrl);
                // Same auth/base-address setup OpenClient(apiKey, baseUrl) applies internally; building it
                // here lets one client be shared rather than minted (and leaked) per agent run.
                _httpClient = HttpClientFactory.CreateForOpenAI(apiKey, _baseUrl);
            }

            return (_httpClient, _baseUrl!);
        }
    }

    public void Dispose() => _httpClient?.Dispose();
}
