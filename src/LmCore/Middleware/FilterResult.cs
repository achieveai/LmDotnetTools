namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Represents the result of a function filtering operation, including the reason for filtering.
/// </summary>
public class FilterResult
{
    /// <summary>
    ///     Private constructor to enforce factory method usage.
    /// </summary>
    private FilterResult(bool isFiltered, string reason, FilterRuleType ruleType, string? matchedPattern = null)
    {
        IsFiltered = isFiltered;
        Reason = reason;
        RuleType = ruleType;
        MatchedPattern = matchedPattern;
    }

    /// <summary>
    ///     Gets whether the function should be filtered out (excluded).
    ///     True means the function is filtered out, False means it's included.
    /// </summary>
    public bool IsFiltered { get; }

    /// <summary>
    ///     Gets the reason why the function was filtered or included.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    ///     Gets the type of filtering rule that was applied.
    /// </summary>
    public FilterRuleType RuleType { get; }

    /// <summary>
    ///     Gets the specific pattern or rule that matched (if applicable).
    /// </summary>
    public string? MatchedPattern { get; }

    /// <summary>
    ///     Creates a result indicating the function is included (not filtered).
    /// </summary>
    /// <param name="reason">The reason for inclusion</param>
    /// <returns>A FilterResult indicating inclusion</returns>
    public static FilterResult Include(string reason = "Function passed all filters")
    {
        return new FilterResult(false, reason, FilterRuleType.None);
    }

    /// <summary>
    ///     Creates a result indicating the function is filtered out due to provider being disabled.
    /// </summary>
    /// <param name="providerName">The name of the disabled provider</param>
    /// <returns>A FilterResult indicating exclusion</returns>
    public static FilterResult FilteredByDisabledProvider(string providerName)
    {
        return new FilterResult(true, $"Provider '{providerName}' is disabled", FilterRuleType.ProviderDisabled);
    }

    /// <summary>
    ///     Creates a result indicating the function is filtered out by a provider block list.
    /// </summary>
    /// <param name="providerName">The provider name</param>
    /// <param name="pattern">The pattern that matched</param>
    /// <returns>A FilterResult indicating exclusion</returns>
    public static FilterResult FilteredByProviderBlockList(string providerName, string pattern)
    {
        return new FilterResult(
            true,
            $"Function blocked by provider '{providerName}' deny list pattern: {pattern}",
            FilterRuleType.ProviderBlockList,
            pattern
        );
    }

    /// <summary>
    ///     Creates a result indicating the function is filtered out because it's not in the provider allow list.
    /// </summary>
    /// <param name="providerName">The provider name</param>
    /// <returns>A FilterResult indicating exclusion</returns>
    public static FilterResult FilteredByProviderAllowList(string providerName)
    {
        return new FilterResult(
            true,
            $"Function not in provider '{providerName}' allow list",
            FilterRuleType.ProviderAllowList
        );
    }

    /// <summary>
    ///     Creates a result indicating the function is filtered out by the global block list.
    /// </summary>
    /// <param name="pattern">The pattern that matched</param>
    /// <returns>A FilterResult indicating exclusion</returns>
    public static FilterResult FilteredByGlobalBlockList(string pattern)
    {
        return new FilterResult(
            true,
            $"Function blocked by global deny list pattern: {pattern}",
            FilterRuleType.GlobalBlockList,
            pattern
        );
    }

    /// <summary>
    ///     Creates a result indicating the function is filtered out because it's not in the global allow list.
    /// </summary>
    /// <returns>A FilterResult indicating exclusion</returns>
    public static FilterResult FilteredByGlobalAllowList()
    {
        return new FilterResult(true, "Function not in global allow list", FilterRuleType.GlobalAllowList);
    }

    /// <summary>
    ///     Creates a result indicating filtering is disabled.
    /// </summary>
    /// <returns>A FilterResult indicating inclusion due to disabled filtering</returns>
    public static FilterResult FilteringDisabled()
    {
        return new FilterResult(false, "Function filtering is disabled", FilterRuleType.None);
    }

    /// <summary>
    ///     Returns a string representation of the filter result.
    /// </summary>
    public override string ToString()
    {
        var status = IsFiltered ? "Filtered" : "Included";
        return MatchedPattern != null
            ? $"{status}: {Reason} (Rule: {RuleType}, Pattern: {MatchedPattern})"
            : $"{status}: {Reason} (Rule: {RuleType})";
    }
}

/// <summary>
///     Represents the type of filtering rule that was applied.
/// </summary>
public enum FilterRuleType
{
    /// <summary>
    ///     No filtering rule was applied (function included).
    /// </summary>
    None,

    /// <summary>
    ///     Filtered because the provider is disabled.
    /// </summary>
    ProviderDisabled,

    /// <summary>
    ///     Filtered by provider-specific block list.
    /// </summary>
    ProviderBlockList,

    /// <summary>
    ///     Filtered by provider-specific allow list.
    /// </summary>
    ProviderAllowList,

    /// <summary>
    ///     Filtered by global block list.
    /// </summary>
    GlobalBlockList,

    /// <summary>
    ///     Filtered by global allow list.
    /// </summary>
    GlobalAllowList,
}
