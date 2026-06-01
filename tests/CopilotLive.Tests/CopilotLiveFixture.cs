using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Auth;

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
    private IReadOnlyList<string>? _models;

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
        if (_models is not null)
        {
            return _models;
        }

        await _modelsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_models is not null)
            {
                return _models;
            }

            using var http = CopilotHttpClientFactory.Create(Options.BaseUrl, TokenProvider, Session, Options);
            using var response = await http.GetAsync("/models", cancellationToken).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _models = ParseModelIds(json);
            return _models;
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
            return env!;
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
            return env!;
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

    private static IReadOnlyList<string> ParseModelIds(string json)
    {
        var ids = new List<string>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            ? data
            : root;

        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out var idEl)
                    && idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ids.Add(id!);
                    }
                }
            }
        }

        return ids;
    }
}

/// <summary>xUnit collection so the fixture (token + session + model list) is shared across tests.</summary>
[CollectionDefinition(Name)]
public sealed class CopilotLiveCollection : ICollectionFixture<CopilotLiveFixture>
{
    public const string Name = "copilot-live";
}
