namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Interface for middleware that can provide functions for use with FunctionCallMiddleware
/// </summary>
public interface IFunctionProvider
{
    /// <summary>
    /// Name of this function provider for debugging and conflict resolution
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Priority for ordering providers (lower numbers = higher priority)
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Get all functions provided by this provider
    /// </summary>
    /// <returns>Collection of function descriptors</returns>
    IEnumerable<FunctionDescriptor> GetFunctions();
}
