namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Outcome of an arm attempt. Either the wait was armed (and the caller should park the run), or
/// it was rejected up front with a machine-readable reason the model can act on without parking.
/// </summary>
public sealed record WaitArmResult
{
    private WaitArmResult() { }

    /// <summary>True when the wait was armed; the tool handler should return <c>Deferred()</c>.</summary>
    public bool IsArmed { get; private init; }

    /// <summary>The armed wait's id (present only when <see cref="IsArmed"/>).</summary>
    public string? WaitId { get; private init; }

    /// <summary>Machine-readable rejection reason (present only when not armed).</summary>
    public string? Reason { get; private init; }

    /// <summary>Human-readable rejection detail (present only when not armed).</summary>
    public string? Message { get; private init; }

    /// <summary>Builds an armed result.</summary>
    public static WaitArmResult Accept(string waitId) => new() { IsArmed = true, WaitId = waitId };

    /// <summary>Builds a rejected result.</summary>
    public static WaitArmResult Reject(string reason, string message) =>
        new() { IsArmed = false, Reason = reason, Message = message };
}
