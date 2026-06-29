namespace AchieveAi.LmDotnetTools.Misc.Configuration;

/// <summary>
///     Configuration for the web tools (WebFetch / WebSearch) and their backing provider.
///     Mirrors the <see cref="LlmCacheOptions" /> environment/validation pattern.
/// </summary>
public sealed record WebToolsOptions
{
    /// <summary>
    ///     The backends this build understands. Used to produce a clear validation error.
    /// </summary>
    private static readonly string[] SupportedBackends = ["jina"];

    /// <summary>
    ///     The backend used to satisfy web tool calls. Only <c>"jina"</c> is supported today.
    /// </summary>
    public string Backend { get; init; } = "jina";

    /// <summary>
    ///     API key for the Jina backend. Optional for WebFetch; required for WebSearch.
    /// </summary>
    public string? JinaApiKey { get; init; }

    /// <summary>
    ///     Maximum number of characters of Markdown a tool may return before truncation. Defaults to 50,000.
    /// </summary>
    public int OutputCap { get; init; } = 50_000;

    /// <summary>
    ///     Per-call timeout in milliseconds. Defaults to 30,000 (30 seconds).
    /// </summary>
    public int TimeoutMs { get; init; } = 30_000;

    /// <summary>
    ///     Maximum allowed length of a search query in characters. Defaults to 2,048.
    /// </summary>
    public int MaxQueryLength { get; init; } = 2_048;

    /// <summary>
    ///     Validates the configuration options.
    /// </summary>
    /// <returns>A list of validation errors, or an empty list if valid.</returns>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (
            string.IsNullOrWhiteSpace(Backend)
            || !SupportedBackends.Contains(Backend.Trim(), StringComparer.OrdinalIgnoreCase)
        )
        {
            errors.Add(
                $"Backend '{Backend}' is not supported. Supported backends: {string.Join(", ", SupportedBackends)}."
            );
        }

        if (OutputCap <= 0)
        {
            errors.Add("OutputCap must be greater than zero.");
        }

        if (TimeoutMs <= 0)
        {
            errors.Add("TimeoutMs must be greater than zero.");
        }

        if (MaxQueryLength <= 0)
        {
            errors.Add("MaxQueryLength must be greater than zero.");
        }

        return errors;
    }

    /// <summary>
    ///     Creates <see cref="WebToolsOptions" /> from environment variables.
    ///     Environment variables:
    ///     - WEB_TOOLS_BACKEND: backend selector (defaults to "jina")
    ///     - JINA_API_KEY: Jina API key (optional; null when unset)
    ///     - WEB_TOOLS_OUTPUT_CAP: output character cap (defaults to 50,000)
    ///     - WEB_TOOLS_TIMEOUT_MS: per-call timeout in milliseconds (defaults to 30,000)
    /// </summary>
    /// <returns><see cref="WebToolsOptions" /> configured from environment variables.</returns>
    public static WebToolsOptions FromEnvironment()
    {
        var backend = Environment.GetEnvironmentVariable("WEB_TOOLS_BACKEND");
        var apiKey = Environment.GetEnvironmentVariable("JINA_API_KEY");
        var outputCap = Environment.GetEnvironmentVariable("WEB_TOOLS_OUTPUT_CAP");
        var timeoutMs = Environment.GetEnvironmentVariable("WEB_TOOLS_TIMEOUT_MS");

        return new WebToolsOptions
        {
            Backend = !string.IsNullOrWhiteSpace(backend) ? backend.Trim() : "jina",
            JinaApiKey = !string.IsNullOrEmpty(apiKey) ? apiKey : null,
            OutputCap =
                !string.IsNullOrEmpty(outputCap) && int.TryParse(outputCap, out var cap) && cap > 0 ? cap : 50_000,
            TimeoutMs =
                !string.IsNullOrEmpty(timeoutMs) && int.TryParse(timeoutMs, out var timeout) && timeout > 0
                    ? timeout
                    : 30_000,
        };
    }
}
