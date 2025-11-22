using AchieveAi.LmDotnetTools.LmCore.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Handles function filtering based on configuration rules for all function providers.
///
/// Thread Safety: This class is immutable after construction and is thread-safe for read operations.
/// The configuration passed to the constructor should not be modified after the FunctionFilter is created.
/// </summary>
public class FunctionFilter
{
    private readonly FunctionFilterConfig? _globalConfig;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the FunctionFilter class
    /// </summary>
    /// <param name="globalConfig">Global function filtering configuration</param>
    /// <param name="logger">Optional logger for debugging</param>
    public FunctionFilter(FunctionFilterConfig? globalConfig, ILogger? logger = null)
    {
        _globalConfig = globalConfig;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Determines whether a function should be filtered out based on configuration
    /// </summary>
    /// <param name="descriptor">The function descriptor to evaluate</param>
    /// <param name="registeredName">The registered name (possibly with prefix)</param>
    /// <returns>True if the function should be filtered out (excluded), false if it should be included</returns>
    [Obsolete("Use ShouldFilterFunctionWithReason instead for better debugging information")]
    public bool ShouldFilterFunction(FunctionDescriptor descriptor, string registeredName)
    {
        var result = ShouldFilterFunctionWithReason(descriptor, registeredName);
        return result.IsFiltered;
    }

    /// <summary>
    /// Determines whether a function should be filtered out based on configuration,
    /// providing detailed information about why the decision was made.
    /// </summary>
    /// <param name="descriptor">The function descriptor to evaluate</param>
    /// <param name="registeredName">The registered name (possibly with prefix)</param>
    /// <returns>A FilterResult containing the filtering decision and reasoning</returns>
    public FilterResult ShouldFilterFunctionWithReason(FunctionDescriptor descriptor, string registeredName)
    {
        // If filtering is not enabled, include all functions
        if (_globalConfig == null || !_globalConfig.EnableFiltering)
        {
            _logger.LogDebug(
                "Function filtering disabled, including function: {Function} from {Provider}",
                descriptor.Contract.Name,
                descriptor.ProviderName
            );
            return FilterResult.FilteringDisabled();
        }

        var providerName = descriptor.ProviderName;
        var originalName = descriptor.Contract.Name;

        // Check if provider is disabled
        if (
            _globalConfig.ProviderConfigs != null
            && _globalConfig.ProviderConfigs.TryGetValue(providerName, out var providerConfig)
        )
        {
            if (!providerConfig.Enabled)
            {
                _logger.LogInformation(
                    "Provider disabled: {Provider}, filtering out function: {Function}",
                    providerName,
                    originalName
                );
                return FilterResult.FilteredByDisabledProvider(providerName);
            }

            // Priority 1: Check provider-specific blocked list
            if (providerConfig.BlockedFunctions != null && providerConfig.BlockedFunctions.Count > 0)
            {
                var blockedPattern = providerConfig.BlockedFunctions.FirstOrDefault(pattern =>
                    MatchesPattern(originalName, pattern)
                );
                if (blockedPattern != null)
                {
                    _logger.LogInformation(
                        "Function blocked by provider config: Provider={Provider}, Function={Function}, Pattern={Pattern}",
                        providerName,
                        originalName,
                        blockedPattern
                    );
                    return FilterResult.FilteredByProviderBlockList(providerName, blockedPattern);
                }
            }

            // Priority 2: Check provider-specific allowed list
            if (providerConfig.AllowedFunctions != null && providerConfig.AllowedFunctions.Count > 0)
            {
                var isAllowed = providerConfig.AllowedFunctions.Any(pattern => MatchesPattern(originalName, pattern));

                if (!isAllowed)
                {
                    _logger.LogDebug(
                        "Function not in provider allowed list: Provider={Provider}, Function={Function}",
                        providerName,
                        originalName
                    );
                    return FilterResult.FilteredByProviderAllowList(providerName);
                }
            }
        }

        // Priority 3: Check global blocked list
        if (_globalConfig.GlobalBlockedFunctions != null && _globalConfig.GlobalBlockedFunctions.Count > 0)
        {
            // Check against both registered name and provider-prefixed pattern
            var providerPrefixedPattern = $"{providerName}__{originalName}";

            var blockedPattern = _globalConfig.GlobalBlockedFunctions.FirstOrDefault(pattern =>
                MatchesPattern(registeredName, pattern)
                || MatchesPattern(providerPrefixedPattern, pattern)
                || MatchesPattern(originalName, pattern)
            );

            if (blockedPattern != null)
            {
                _logger.LogInformation(
                    "Function blocked by global config: Function={Function}, RegisteredName={RegisteredName}, Pattern={Pattern}",
                    originalName,
                    registeredName,
                    blockedPattern
                );
                return FilterResult.FilteredByGlobalBlockList(blockedPattern);
            }
        }

        // Priority 4: Check global allowed list (if specified, only these are allowed)
        if (_globalConfig.GlobalAllowedFunctions != null && _globalConfig.GlobalAllowedFunctions.Count > 0)
        {
            // Check against both registered name and provider-prefixed pattern
            var providerPrefixedPattern = $"{providerName}__{originalName}";

            var isAllowed = _globalConfig.GlobalAllowedFunctions.Any(pattern =>
                MatchesPattern(registeredName, pattern)
                || MatchesPattern(providerPrefixedPattern, pattern)
                || MatchesPattern(originalName, pattern)
            );

            if (!isAllowed)
            {
                _logger.LogDebug(
                    "Function not in global allowed list: Function={Function}, RegisteredName={RegisteredName}",
                    originalName,
                    registeredName
                );
                return FilterResult.FilteredByGlobalAllowList();
            }
        }

        _logger.LogDebug(
            "Function passed all filters, including: Function={Function}, RegisteredName={RegisteredName}, Provider={Provider}",
            originalName,
            registeredName,
            providerName
        );
        return FilterResult.Include($"Function '{originalName}' from provider '{providerName}' passed all filters");
    }

    /// <summary>
    /// Filters a collection of function descriptors based on configuration
    /// </summary>
    /// <param name="descriptors">The functions to filter</param>
    /// <param name="namingMap">Optional naming map for registered names</param>
    /// <returns>Filtered collection of function descriptors</returns>
    public IEnumerable<FunctionDescriptor> FilterFunctions(
        IEnumerable<FunctionDescriptor> descriptors,
        Dictionary<string, string>? namingMap = null
    )
    {
        if (_globalConfig == null || !_globalConfig.EnableFiltering)
        {
            _logger.LogDebug("Function filtering disabled, returning all functions");
            return descriptors;
        }

        var filtered = new List<FunctionDescriptor>();
        var totalCount = 0;
        var filteredCount = 0;

        foreach (var descriptor in descriptors)
        {
            totalCount++;

            // Get the registered name from naming map if available
            var registeredName =
                namingMap?.TryGetValue(descriptor.Key, out var name) == true ? name : descriptor.Contract.Name;

            if (!ShouldFilterFunctionWithReason(descriptor, registeredName).IsFiltered)
            {
                filtered.Add(descriptor);
            }
            else
            {
                filteredCount++;
            }
        }

        _logger.LogInformation(
            "Function filtering completed: Total={Total}, Included={Included}, Filtered={Filtered}",
            totalCount,
            filtered.Count,
            filteredCount
        );

        return filtered;
    }

    /// <summary>
    /// Checks if a text matches a pattern with wildcard support
    /// </summary>
    /// <param name="text">The text to match</param>
    /// <param name="pattern">The pattern to match against (supports * wildcard)</param>
    /// <returns>True if the text matches the pattern</returns>
    private bool MatchesPattern(string text, string pattern)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        // Handle exact match for "*"
        if (pattern == "*")
        {
            return true;
        }

        // Handle prefix wildcard (e.g., "github__*")
        if (pattern.EndsWith("*") && !pattern.StartsWith("*"))
        {
            var prefix = pattern.Substring(0, pattern.Length - 1);
            var matches = text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                _logger.LogDebug("Pattern match (prefix): Text={Text}, Pattern={Pattern}", text, pattern);
            }

            return matches;
        }

        // Handle suffix wildcard (e.g., "*_search")
        if (pattern.StartsWith("*") && !pattern.EndsWith("*"))
        {
            var suffix = pattern.Substring(1);
            var matches = text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                _logger.LogDebug("Pattern match (suffix): Text={Text}, Pattern={Pattern}", text, pattern);
            }

            return matches;
        }

        // Handle contains wildcard (e.g., "*search*")
        if (pattern.StartsWith("*") && pattern.EndsWith("*") && pattern.Length > 2)
        {
            var middle = pattern.Substring(1, pattern.Length - 2);
            var matches = text.Contains(middle, StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                _logger.LogDebug("Pattern match (contains): Text={Text}, Pattern={Pattern}", text, pattern);
            }

            return matches;
        }

        // Handle exact match (no wildcards)
        var exactMatch = string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);

        if (exactMatch)
        {
            _logger.LogDebug("Pattern match (exact): Text={Text}, Pattern={Pattern}", text, pattern);
        }

        return exactMatch;
    }
}
