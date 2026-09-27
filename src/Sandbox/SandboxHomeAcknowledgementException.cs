namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>A create succeeded, but Gateway did not acknowledge its requested native home.
/// The new session must not be used. Direct SDK callers retain its identity for explicit cleanup.</summary>
public sealed class SandboxHomeAcknowledgementException : Exception
{
    public string CreatedSessionId { get; }
    public string RequestedHome { get; }
    public string? AcknowledgedHome { get; }

    /// <summary>Null when no cleanup was attempted (direct SDK caller); true after acknowledged deletion;
    /// false when cleanup failed. A failure retains its diagnostic in <see cref="Exception.InnerException"/>.</summary>
    public bool? CleanupSucceeded { get; }

    public SandboxHomeAcknowledgementException(
        string createdSessionId,
        string requestedHome,
        string? acknowledgedHome,
        bool? cleanupSucceeded = null,
        Exception? cleanupFailure = null
    )
        : base(
            "Gateway created a session without acknowledging the requested native workspace home. "
                + "Upgrade or correct the Gateway home configuration; the created session must not be used.",
            cleanupFailure
        )
    {
        CreatedSessionId = createdSessionId;
        RequestedHome = requestedHome;
        AcknowledgedHome = acknowledgedHome;
        CleanupSucceeded = cleanupSucceeded;
    }
}
