using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Handles collision detection and name resolution for functions across all providers
/// </summary>
public partial class FunctionCollisionDetector
{
    private static readonly Regex InvalidCharPattern = MyRegex();
    private static readonly Regex MultipleUnderscorePattern = MyRegex1();
    private readonly ILogger _logger;

    /// <summary>
    ///     Initializes a new instance of the FunctionCollisionDetector class
    /// </summary>
    /// <param name="logger">Optional logger for debugging</param>
    public FunctionCollisionDetector(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    ///     Detects name collisions among functions from different providers and resolves them
    /// </summary>
    /// <param name="functions">Collection of function descriptors from all providers</param>
    /// <param name="config">Configuration for collision handling</param>
    /// <returns>Dictionary mapping function keys to their registered names</returns>
    public Dictionary<string, string> DetectAndResolveCollisions(
        IEnumerable<FunctionDescriptor> functions,
        FunctionFilterConfig? config = null
    )
    {
        var usePrefixOnlyForCollisions = config?.UsePrefixOnlyForCollisions ?? true;

        _logger.LogDebug(
            "Starting collision detection: UsePrefixOnlyForCollisions={UsePrefixOnlyForCollisions}",
            usePrefixOnlyForCollisions
        );

        // Step 1: Group functions by their base name
        ArgumentNullException.ThrowIfNull(functions);
        var functionGroups = new Dictionary<string, List<FunctionDescriptor>>();

        foreach (var function in functions)
        {
            var baseName = function.Contract.Name;
            if (!functionGroups.TryGetValue(baseName, out var value))
            {
                value = [];
                functionGroups[baseName] = value;
            }

            value.Add(function);
        }

        _logger.LogDebug(
            "Function groups built: UniqueNames={UniqueCount}, TotalFunctions={TotalCount}",
            functionGroups.Count,
            functionGroups.Values.Sum(list => list.Count)
        );

        // Step 2: Detect collisions and determine final names
        var namingMap = new Dictionary<string, string>();

        foreach (var (baseName, descriptors) in functionGroups)
        {
            var hasCollision = descriptors.Count > 1;

            if (hasCollision)
            {
                var providers = descriptors.Select(d => d.ProviderName).Distinct();
                _logger.LogInformation(
                    "Function collision detected: FunctionName={FunctionName}, ProviderCount={ProviderCount}, Providers={Providers}",
                    baseName,
                    descriptors.Count,
                    string.Join(", ", providers)
                );
            }

            foreach (var descriptor in descriptors)
            {
                var registeredName = DetermineRegisteredName(
                    descriptor,
                    hasCollision,
                    usePrefixOnlyForCollisions,
                    config
                );

                namingMap[descriptor.Key] = registeredName;

                _logger.LogDebug(
                    "Function naming resolved: Original={Original}, Provider={Provider}, Registered={Registered}, HasCollision={HasCollision}",
                    descriptor.Contract.Name,
                    descriptor.ProviderName,
                    registeredName,
                    hasCollision
                );
            }
        }

        _logger.LogInformation(
            "Collision detection completed: TotalFunctions={TotalFunctions}, CollisionsResolved={CollisionCount}",
            namingMap.Count,
            functionGroups.Count(kvp => kvp.Value.Count > 1)
        );

        return namingMap;
    }

    /// <summary>
    ///     Determines the registered name for a function based on collision and configuration
    /// </summary>
    private static string DetermineRegisteredName(
        FunctionDescriptor descriptor,
        bool hasCollision,
        bool usePrefixOnlyForCollisions,
        FunctionFilterConfig? config
    )
    {
        var providerName = descriptor.ProviderName;
        var functionName = descriptor.Contract.Name;

        // Check for custom prefix in provider config
        string? customPrefix = null;
        if (config?.ProviderConfigs != null && config.ProviderConfigs.TryGetValue(providerName, out var providerConfig))
        {
            customPrefix = providerConfig.CustomPrefix;
        }

        // Determine if we need to prefix this function
        var needsPrefix = hasCollision || !usePrefixOnlyForCollisions;

        if (needsPrefix)
        {
            // Use custom prefix if provided, otherwise use provider name
            var prefix = customPrefix ?? providerName;
            var sanitizedPrefix = SanitizeName(prefix);
            var sanitizedFunctionName = SanitizeName(functionName);

            return $"{sanitizedPrefix}-{sanitizedFunctionName}";
        }

        // No prefix needed, just sanitize the name
        return SanitizeName(functionName);
    }

    /// <summary>
    ///     Sanitizes a name to comply with OpenAI's function name requirements
    ///     OpenAI requires function names to match pattern: ^[a-zA-Z0-9_-]+$
    /// </summary>
    /// <param name="name">The name to sanitize</param>
    /// <returns>Sanitized name that complies with OpenAI requirements</returns>
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "unknown";
        }

        // Replace invalid characters with underscores
        var sanitized = InvalidCharPattern.Replace(name, "_");

        // Replace multiple consecutive underscores with single underscore
        sanitized = MultipleUnderscorePattern.Replace(sanitized, "_");

        // Ensure it doesn't start with a number (OpenAI requirement)
        if (sanitized.Length > 0 && char.IsDigit(sanitized[0]))
        {
            sanitized = "_" + sanitized;
        }

        // Ensure it's not empty or only non-alphanumeric after sanitization
        if (string.IsNullOrEmpty(sanitized) || !sanitized.Any(char.IsLetterOrDigit))
        {
            sanitized = "sanitized_function";
        }

        return sanitized;
    }

    /// <summary>
    ///     Analyzes function collections for potential collisions without resolving them
    /// </summary>
    /// <param name="functions">Collection of function descriptors</param>
    /// <returns>Analysis report of collisions</returns>
    public static CollisionAnalysisReport AnalyzeCollisions(IEnumerable<FunctionDescriptor> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        var report = new CollisionAnalysisReport();
        var functionGroups = new Dictionary<string, List<FunctionDescriptor>>();

        // Group functions by name
        foreach (var function in functions)
        {
            var baseName = function.Contract.Name;
            if (!functionGroups.TryGetValue(baseName, out var value))
            {
                value = [];
                functionGroups[baseName] = value;
            }

            value.Add(function);
            report.TotalFunctions++;
        }

        // Analyze collisions
        foreach (var (functionName, descriptors) in functionGroups)
        {
            if (descriptors.Count > 1)
            {
                var collision = new CollisionInfo
                {
                    FunctionName = functionName,
                    Providers = [.. descriptors.Select(d => d.ProviderName).Distinct()],
                    Count = descriptors.Count,
                };
                report.Collisions.Add(collision);
            }
        }

        report.UniqueNames = functionGroups.Count;
        report.CollisionCount = report.Collisions.Count;

        return report;
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_-]", RegexOptions.Compiled)]
    private static partial Regex MyRegex();

    [GeneratedRegex(@"_{2,}", RegexOptions.Compiled)]
    private static partial Regex MyRegex1();
}

/// <summary>
///     Report containing collision analysis information
/// </summary>
public class CollisionAnalysisReport
{
    public int TotalFunctions { get; set; }
    public int UniqueNames { get; set; }
    public int CollisionCount { get; set; }
    public List<CollisionInfo> Collisions { get; set; } = [];
}

/// <summary>
///     Information about a specific function name collision
/// </summary>
public class CollisionInfo
{
    public string FunctionName { get; set; } = string.Empty;
    public List<string> Providers { get; set; } = [];
    public int Count { get; set; }
}
