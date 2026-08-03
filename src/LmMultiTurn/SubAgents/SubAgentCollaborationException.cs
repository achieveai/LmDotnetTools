namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>Why a collaboration refused a spawn.</summary>
public static class SubAgentCollaborationFailureCodes
{
    /// <summary>No usable role was available for the sub-agent.</summary>
    public const string InvalidRole = "invalid_role";

    /// <summary>No usable description was available for the sub-agent.</summary>
    public const string InvalidDescription = "invalid_description";

    /// <summary>The role or description was outside its length bounds.</summary>
    public const string InvalidMetadata = "invalid_metadata";

    /// <summary>Spawning would exceed the collaboration's maximum delegation depth.</summary>
    public const string DepthLimit = "depth_limit";

    /// <summary>The collaboration already holds its maximum number of agents.</summary>
    public const string CapacityExhausted = "capacity_exhausted";

    /// <summary>The directory refused the registration for a structural reason.</summary>
    public const string RegistrationFailed = "registration_failed";
}

/// <summary>
/// A spawn the collaboration refused, carrying the code the calling model needs to correct itself.
/// </summary>
/// <remarks>
/// Separate from <see cref="SubAgentExecutionException"/> because none of these are run failures: the
/// sub-agent never started. The <see cref="FailureCode"/> travels to the tool boundary so the caller
/// sees <c>capacity_exhausted</c> or <c>invalid_role</c> rather than a generic failure it cannot act on.
/// </remarks>
public sealed class SubAgentCollaborationException : InvalidOperationException
{
    /// <summary>Creates a refusal carrying a machine-readable code.</summary>
    /// <param name="failureCode">A code from <see cref="SubAgentCollaborationFailureCodes"/>.</param>
    /// <param name="message">
    /// Message surfaced verbatim to the calling model. Must stay content-free: never echo the role,
    /// description, or prompt back into it.
    /// </param>
    public SubAgentCollaborationException(string failureCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        FailureCode = failureCode;
    }

    /// <summary>The machine-readable reason the spawn was refused.</summary>
    public string FailureCode { get; }
}
