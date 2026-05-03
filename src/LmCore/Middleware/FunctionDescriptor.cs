using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;

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
    ///     The function handler that executes the actual function logic.
    ///     Receives raw JSON args and a <see cref="ToolCallContext"/> carrying the call's
    ///     <c>tool_call_id</c> and the host's <see cref="CancellationToken"/>.
    ///     Returns a <see cref="ToolHandlerResult"/> — build one with
    ///     <see cref="ToolHandlerResult.FromText(string)"/>,
    ///     <see cref="ToolHandlerResult.FromError(string, string?)"/>, or
    ///     <see cref="ToolHandlerResult.FromMultiModal(string, IList{ToolResultContentBlock})"/>;
    ///     or return <see cref="ToolHandlerResult.Deferred"/> to signal that the result will
    ///     be supplied later via <c>MultiTurnAgentLoop.ResolveToolCallAsync</c>.
    /// </summary>
    public required ToolHandler Handler { get; init; }

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
