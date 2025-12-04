using AchieveAi.LmDotnetTools.LmCore.Core;
namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Represents a complete function definition with its contract and handler
/// </summary>
public record FunctionDescriptor
{
    /// <summary>
    ///     The function contract containing metadata and parameter definitions
    /// </summary>
    public required FunctionContract Contract { get; init; }

    /// <summary>
    ///     The function handler that executes the actual function logic
    /// </summary>
    public required Func<string, Task<string>> Handler { get; init; }

    /// <summary>
    ///     Unique key for this function (handles class name prefixing for MCP functions)
    /// </summary>
    public string Key => Contract.ClassName != null ? $"{Contract.ClassName}-{Contract.Name}" : Contract.Name;

    /// <summary>
    ///     Display name for error messages and logging
    /// </summary>
    public string DisplayName => Contract.ClassName != null ? $"{Contract.ClassName}.{Contract.Name}" : Contract.Name;

    /// <summary>
    ///     Provider name for debugging and conflict resolution
    /// </summary>
    public string ProviderName { get; init; } = "Unknown";

    /// <summary>
    ///     Indicates whether this function is stateful (requires per-call instance).
    ///     Stateless functions can be safely reused across multiple LLM invocations.
    ///     Default is false (stateless).
    /// </summary>
    public bool IsStateful { get; init; }
}
