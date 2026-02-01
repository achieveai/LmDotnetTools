using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
///     Base class for Anthropic built-in tools that execute on the server.
///     These tools have a type field that indicates the tool version.
/// </summary>
public abstract record AnthropicBuiltInTool
{
    /// <summary>
    ///     The versioned type identifier for the tool (e.g., "web_search_20250305").
    /// </summary>
    [JsonPropertyName("type")]
    public abstract string Type { get; }

    /// <summary>
    ///     The name of the tool.
    /// </summary>
    [JsonPropertyName("name")]
    public abstract string Name { get; }
}

/// <summary>
///     Web search tool for searching the web using Claude's built-in capability.
/// </summary>
public record AnthropicWebSearchTool : AnthropicBuiltInTool
{
    /// <inheritdoc />
    public override string Type => "web_search_20250305";

    /// <inheritdoc />
    public override string Name => "web_search";

    /// <summary>
    ///     Maximum number of search uses in a single response.
    /// </summary>
    [JsonPropertyName("max_uses")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxUses { get; init; }

    /// <summary>
    ///     Only allow searches on these domains.
    /// </summary>
    [JsonPropertyName("allowed_domains")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedDomains { get; init; }

    /// <summary>
    ///     Block searches on these domains.
    /// </summary>
    [JsonPropertyName("blocked_domains")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? BlockedDomains { get; init; }

    /// <summary>
    ///     User location for localized search results.
    /// </summary>
    [JsonPropertyName("user_location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UserLocation? UserLocation { get; init; }
}

/// <summary>
///     Web fetch tool for fetching content from URLs using Claude's built-in capability.
/// </summary>
public record AnthropicWebFetchTool : AnthropicBuiltInTool
{
    /// <inheritdoc />
    public override string Type => "web_fetch_20250910";

    /// <inheritdoc />
    public override string Name => "web_fetch";

    /// <summary>
    ///     Maximum number of fetch uses in a single response.
    /// </summary>
    [JsonPropertyName("max_uses")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxUses { get; init; }

    /// <summary>
    ///     Only allow fetching from these domains.
    /// </summary>
    [JsonPropertyName("allowed_domains")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedDomains { get; init; }

    /// <summary>
    ///     Block fetching from these domains.
    /// </summary>
    [JsonPropertyName("blocked_domains")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? BlockedDomains { get; init; }

    /// <summary>
    ///     Configuration for enabling citations in fetched content.
    /// </summary>
    [JsonPropertyName("citations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CitationsConfig? Citations { get; init; }

    /// <summary>
    ///     Maximum content tokens to return from fetched pages.
    /// </summary>
    [JsonPropertyName("max_content_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxContentTokens { get; init; }
}

/// <summary>
///     Code execution tool for running code in a sandbox using Claude's built-in capability.
/// </summary>
public record AnthropicCodeExecutionTool : AnthropicBuiltInTool
{
    /// <inheritdoc />
    public override string Type => "code_execution_20250825";

    /// <inheritdoc />
    public override string Name => "code_execution";
}

/// <summary>
///     User location information for localized tool results.
/// </summary>
public record UserLocation
{
    /// <summary>
    ///     The type of location (typically "approximate").
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "approximate";

    /// <summary>
    ///     The city name.
    /// </summary>
    [JsonPropertyName("city")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? City { get; init; }

    /// <summary>
    ///     The region/state name.
    /// </summary>
    [JsonPropertyName("region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Region { get; init; }

    /// <summary>
    ///     The country code (e.g., "US", "GB").
    /// </summary>
    [JsonPropertyName("country")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Country { get; init; }

    /// <summary>
    ///     The timezone (e.g., "America/New_York").
    /// </summary>
    [JsonPropertyName("timezone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Timezone { get; init; }
}

/// <summary>
///     Configuration for enabling citations in built-in tool results.
/// </summary>
public record CitationsConfig
{
    /// <summary>
    ///     Whether citations are enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}

/// <summary>
///     Container information returned in responses for code execution.
///     Used to reuse the same sandbox across multiple turns.
/// </summary>
public record ContainerInfo
{
    /// <summary>
    ///     The container ID for reuse in subsequent requests.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}
