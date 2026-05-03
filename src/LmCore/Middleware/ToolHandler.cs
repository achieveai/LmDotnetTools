using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Tool handler signature used by <see cref="FunctionRegistry"/> and
/// <see cref="FunctionCallMiddleware"/>. Handlers receive raw JSON args, a
/// <see cref="ToolCallContext"/>, and a <see cref="CancellationToken"/>, and return
/// a <see cref="ToolHandlerResult"/> (either <see cref="ToolHandlerResult.Resolved"/>
/// or <see cref="ToolHandlerResult.Deferred"/>).
/// </summary>
public delegate Task<ToolHandlerResult> ToolHandler(
    string argsJson,
    ToolCallContext context,
    CancellationToken cancellationToken);

/// <summary>
/// Post-adapter shape produced by <see cref="FunctionCallMiddleware"/> and consumed by
/// <see cref="ToolCallExecutor"/>. Resolved/Deferred have already been reconciled into a
/// single <see cref="ToolCallResult"/> with deferral fields populated. External callers
/// invoking <see cref="ToolCallExecutor.ExecuteAsync"/> directly author handlers in this
/// shape; most users register <see cref="ToolHandler"/>-shaped handlers via
/// <see cref="FunctionRegistry"/> and let the middleware perform the adaptation.
/// </summary>
public delegate Task<ToolCallResult> ToolCallResultHandler(
    string argsJson,
    ToolCallContext context,
    CancellationToken cancellationToken);
