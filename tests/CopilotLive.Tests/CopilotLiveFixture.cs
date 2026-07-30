using System.Text.Json;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;

namespace AchieveAi.LmDotnetTools.CopilotLive.Tests;

/// <summary>
///     Shared state for the live Copilot suite: resolves the developer's existing GitHub Copilot
///     credential once, holds a single <see cref="CopilotSessionContext"/> (so every test in the run
///     shares one client-session id, like a real CLI session), and lazily lists the available models
///     so chat tests can pick valid model ids without hard-coding names that drift over time.
/// </summary>
public sealed class CopilotLiveFixture
{
    private readonly SemaphoreSlim _modelsGate = new(1, 1);
    private IReadOnlyList<CopilotCatalogEntry>? _catalog;

    public CopilotLiveFixture()
    {
        var cli = new CliCredentialCopilotTokenProvider();
        var token = cli.ResolveToken();

        Available = token is not null;
        SkipReason = Available
            ? string.Empty
            : "No GitHub Copilot credential found. Log in with the GitHub Copilot CLI or `gh auth login`, "
                + "or set GITHUB_COPILOT_TOKEN / GH_TOKEN, then re-run.";

        TokenProvider = cli;
        Session = new CopilotSessionContext();
        Options = new CopilotOptions();
    }

    /// <summary>True when a Copilot credential was found and live tests should run.</summary>
    public bool Available { get; }

    /// <summary>Human-readable reason shown when <see cref="Available"/> is false.</summary>
    public string SkipReason { get; }

    public ICopilotTokenProvider TokenProvider { get; }

    public CopilotSessionContext Session { get; }

    public CopilotOptions Options { get; }

    /// <summary>Lists model ids from <c>GET {host}/models</c> (cached for the run).</summary>
    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken)
    {
        var catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        return [.. catalog.Select(entry => entry.Id)];
    }

    /// <summary>
    ///     Lists every model from <c>GET {host}/models</c> with its vendor and the transports it
    ///     advertises (cached for the run). Selecting a model by the endpoints it ADVERTISES rather
    ///     than by the shape of its id is what keeps a probe from silently testing the wrong quadrant.
    /// </summary>
    public async Task<IReadOnlyList<CopilotCatalogEntry>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        if (_catalog is not null)
        {
            return _catalog;
        }

        await _modelsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_catalog is not null)
            {
                return _catalog;
            }

            using var http = CopilotHttpClientFactory.Create(Options.BaseUrl, TokenProvider, Session, Options);
            using var response = await http.GetAsync("/models", cancellationToken).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _catalog = ParseCatalog(json);
            return _catalog;
        }
        finally
        {
            _ = _modelsGate.Release();
        }
    }

    /// <summary>
    ///     Resolves the Anthropic model id to use: the <c>COPILOT_ANTHROPIC_MODEL</c> env override, else
    ///     the cheapest-looking Claude model exposed by <c>/models</c>, else a sensible default.
    /// </summary>
    public async Task<string> ResolveAnthropicModelAsync(CancellationToken cancellationToken)
    {
        var env = Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_MODEL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        var models = await GetModelsAsync(cancellationToken).ConfigureAwait(false);
        return PickPreferred(models, "claude", ["haiku", "sonnet"]) ?? "claude-sonnet-4.5";
    }

    /// <summary>
    ///     Resolves the OpenAI model id to use: the <c>COPILOT_OPENAI_MODEL</c> env override, else the
    ///     cheapest-looking GPT model exposed by <c>/models</c>, else a sensible default.
    /// </summary>
    public async Task<string> ResolveOpenAiModelAsync(CancellationToken cancellationToken)
    {
        var env = Environment.GetEnvironmentVariable("COPILOT_OPENAI_MODEL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        var models = await GetModelsAsync(cancellationToken).ConfigureAwait(false);
        return PickPreferred(models, "gpt", ["nano", "mini"]) ?? "gpt-4.1";
    }

    private static string? PickPreferred(IReadOnlyList<string> models, string family, string[] cheapHints)
    {
        var candidates = models.Where(m => m.Contains(family, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (var hint in cheapHints)
        {
            var match = candidates.FirstOrDefault(m => m.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return candidates[0];
    }

    private static IReadOnlyList<CopilotCatalogEntry> ParseCatalog(string json)
    {
        var entries = new List<CopilotCatalogEntry>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            ? data
            : root;

        if (list.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var idEl)
                || idEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idEl.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var vendor = item.TryGetProperty("vendor", out var vendorEl) && vendorEl.ValueKind == JsonValueKind.String
                ? vendorEl.GetString() ?? string.Empty
                : string.Empty;

            var endpoints =
                item.TryGetProperty("supported_endpoints", out var endpointsEl)
                && endpointsEl.ValueKind == JsonValueKind.Array
                    ? endpointsEl.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
                    : [];

            entries.Add(new CopilotCatalogEntry(id, vendor, endpoints));
        }

        return entries;
    }
}

/// <summary>One model as Copilot's <c>/models</c> describes it: id, vendor, and advertised transports.</summary>
public sealed record CopilotCatalogEntry(string Id, string Vendor, IReadOnlyList<string> Endpoints)
{
    /// <summary>True when this model advertises <paramref name="endpoint"/> (case-insensitive).</summary>
    public bool Advertises(string endpoint) => Endpoints.Contains(endpoint, StringComparer.OrdinalIgnoreCase);
}

/// <summary>xUnit collection so the fixture (token + session + model list) is shared across tests.</summary>
[CollectionDefinition(Name)]
public sealed class CopilotLiveCollection : ICollectionFixture<CopilotLiveFixture>
{
    public const string Name = "copilot-live";
}
