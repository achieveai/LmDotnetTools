namespace AchieveAi.LmDotnetTools.LmCore.Configuration;

/// <summary>
/// Configuration for function filtering across all providers (MCP, native, etc.)
/// </summary>
public class FunctionFilterConfig
{
    /// <summary>
    /// Whether to enable function filtering based on configuration
    /// </summary>
    public bool EnableFiltering { get; set; } = false;

    /// <summary>
    /// Global list of allowed function names (supports wildcards)
    /// If specified, only these functions will be available across all providers
    /// </summary>
    public List<string>? GlobalAllowedFunctions { get; set; }

    /// <summary>
    /// Global list of blocked function names (supports wildcards)
    /// These functions will be blocked across all providers
    /// </summary>
    public List<string>? GlobalBlockedFunctions { get; set; }

    /// <summary>
    /// Whether to use prefixes only for functions with name collisions
    /// When true: Only colliding functions get prefixed with provider ID
    /// When false: All functions get prefixed with provider ID
    /// </summary>
    public bool UsePrefixOnlyForCollisions { get; set; } = true;

    /// <summary>
    /// Provider-specific filtering configurations
    /// Key is the provider name or type (e.g., "McpServers", "WeatherAPI", "TaskManager")
    /// </summary>
    public Dictionary<string, ProviderFilterConfig>? ProviderConfigs { get; set; }
}

/// <summary>
/// Configuration for filtering functions from a specific provider
/// </summary>
public class ProviderFilterConfig
{
    private string? _customPrefix;

    /// <summary>
    /// Whether this provider is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// List of function names allowed from this provider (supports wildcards)
    /// </summary>
    public List<string>? AllowedFunctions { get; set; }

    /// <summary>
    /// List of function names blocked from this provider (supports wildcards)
    /// </summary>
    public List<string>? BlockedFunctions { get; set; }

    /// <summary>
    /// Custom prefix to use for this provider's functions (optional).
    /// If not specified, the provider name will be used.
    /// The prefix must comply with OpenAI function naming requirements:
    /// - Only letters (a-z, A-Z), numbers (0-9), underscores (_), and hyphens (-) are allowed
    /// - Maximum recommended length is 32 characters to leave room for function names
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the prefix contains invalid characters or exceeds length limits</exception>
    public string? CustomPrefix
    {
        get => _customPrefix;
        set
        {
            if (value != null && !FunctionNameValidator.IsValidPrefix(value))
            {
                throw new ArgumentException(
                    FunctionNameValidator.GetPrefixValidationError(value),
                    nameof(CustomPrefix)
                );
            }
            _customPrefix = value;
        }
    }

    /// <summary>
    /// Validates that the configuration is correct and all constraints are met.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when validation fails</exception>
    public void Validate()
    {
        // Validate custom prefix if set
        if (CustomPrefix != null && !FunctionNameValidator.IsValidPrefix(CustomPrefix))
        {
            throw new ArgumentException(
                FunctionNameValidator.GetPrefixValidationError(CustomPrefix),
                nameof(CustomPrefix)
            );
        }
    }
}

/// <summary>
/// Legacy compatibility: Maps to FunctionFilterConfig
/// </summary>
public class McpToolFilterConfig : FunctionFilterConfig
{
    public McpToolFilterConfig()
    {
        // For backward compatibility, MCP tools use the same configuration
    }
}

/// <summary>
/// Legacy compatibility: Maps to ProviderFilterConfig
/// </summary>
public class McpServerFilterConfig : ProviderFilterConfig
{
    public McpServerFilterConfig()
    {
        // For backward compatibility
    }
}
