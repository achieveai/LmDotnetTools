using System.Diagnostics.CodeAnalysis;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     A function provider whose tools are not all advertised on every request, and which can turn one of its
///     own calls into a call of another registered tool. The registry keeps every handler either way.
/// </summary>
/// <remarks>
///     <para>
///     <see cref="FunctionRegistry.BuildContracts"/> leaves out every contract that any registered
///     <see cref="IDeferredToolProvider"/> does not advertise. <see cref="FunctionRegistry.Build"/> is
///     unaffected, so handler snapshots, and anything built from them, stay complete.
///     </para>
///     <para>
///     An agent loop asks <see cref="TryUnwrap"/> about each tool call before it looks up a handler or asks
///     for approval, so the target, not the wrapper, is what is approved, run and reported. With no
///     <see cref="IDeferredToolProvider"/> registered, neither the contracts nor the dispatch change.
///     </para>
/// </remarks>
public interface IDeferredToolProvider : IFunctionProvider
{
    /// <summary>
    ///     False keeps the handler but leaves this contract out of the request's tool list.
    /// </summary>
    /// <remarks>
    ///     Must return the same answer between two of the owner's rotations: the tool list is part of the
    ///     prompt the provider caches.
    /// </remarks>
    /// <param name="functionName">The registered (possibly collision-renamed) function name.</param>
    bool IsAdvertised(string functionName);

    /// <summary>
    ///     Turns one of this provider's calls into a call of another registered tool, or refuses it.
    ///     Called before handler lookup and approval.
    /// </summary>
    /// <remarks>
    ///     A <see cref="ToolCallUnwrap.Run"/> target must complete within the run and must not return a
    ///     deferred result: a loop that rebuilds deferred calls from history after a restart reads the
    ///     model's own call, which is the wrapper, not the target.
    /// </remarks>
    /// <param name="functionName">The function name the model called.</param>
    /// <param name="argumentsJson">The raw argument JSON the model sent.</param>
    /// <returns>Null when the call is not this provider's (the loop runs it as is).</returns>
    ToolCallUnwrap? TryUnwrap(string functionName, string argumentsJson);
}

/// <summary>
///     What <see cref="IDeferredToolProvider.TryUnwrap"/> decided: run another registered tool, or refuse
///     the call. Closed: exactly one of <see cref="Run"/> or <see cref="Refuse"/>, so no invalid
///     combination can be built.
/// </summary>
public sealed class ToolCallUnwrap
{
    private ToolCallUnwrap(string? functionName, string? argumentsJson, string? errorJson, string? errorCode)
    {
        FunctionName = functionName;
        ArgumentsJson = argumentsJson;
        ErrorJson = errorJson;
        ErrorCode = errorCode;
    }

    /// <summary>True for a refusal: <see cref="ErrorJson"/> and <see cref="ErrorCode"/> are set.</summary>
    [MemberNotNullWhen(true, nameof(ErrorJson), nameof(ErrorCode))]
    [MemberNotNullWhen(false, nameof(FunctionName), nameof(ArgumentsJson))]
    public bool IsRefusal => ErrorJson is not null;

    /// <summary>The registered tool to run instead. Set exactly when <see cref="IsRefusal"/> is false.</summary>
    public string? FunctionName { get; }

    /// <summary>The target's own argument JSON. Set exactly when <see cref="IsRefusal"/> is false.</summary>
    public string? ArgumentsJson { get; }

    /// <summary>The error result the model gets back. Set exactly when <see cref="IsRefusal"/> is true.</summary>
    public string? ErrorJson { get; }

    /// <summary>The machine-readable refusal code. Set exactly when <see cref="IsRefusal"/> is true.</summary>
    public string? ErrorCode { get; }

    /// <summary>Run <paramref name="functionName"/> with <paramref name="argumentsJson"/> instead of the call.</summary>
    public static ToolCallUnwrap Run(string functionName, string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(functionName);
        ArgumentException.ThrowIfNullOrEmpty(argumentsJson);
        return new ToolCallUnwrap(functionName, argumentsJson, null, null);
    }

    /// <summary>Refuse the call: nothing runs and the model gets <paramref name="errorJson"/> back.</summary>
    public static ToolCallUnwrap Refuse(string errorJson, string errorCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(errorJson);
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        return new ToolCallUnwrap(null, null, errorJson, errorCode);
    }
}
