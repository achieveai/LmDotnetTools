namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Thrown when a conversation owned by one end user is addressed by a request carrying a DIFFERENT
/// end user (P1 spec 7.6).
/// </summary>
/// <remarks>
/// A second, PARALLEL check beside <see cref="SandboxCredentialConflictException"/>, not a
/// replacement for it. The app-id freeze is the tenancy boundary between calling services and
/// removing it would change gateway ownership semantics; this one is the boundary between people.
/// Before P1 both sides of the user comparison were always null, so the guard always matched and
/// meant nothing in the UI - which is exactly what this closes.
/// </remarks>
public sealed class PrincipalConflictException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="threadId">The thread that was addressed.</param>
    /// <param name="existingUserId">The user the thread is bound to, or null when unowned.</param>
    /// <param name="requestedUserId">The user the conflicting request carried, or null.</param>
    public PrincipalConflictException(string threadId, string? existingUserId, string? requestedUserId)
        : base(
            $"Thread '{threadId}' is bound to user '{Describe(existingUserId)}'; the current request "
                + $"carries user '{Describe(requestedUserId)}'. A conversation cannot change its "
                + "owning user identity."
        )
    {
        ThreadId = threadId;
        ExistingUserId = existingUserId;
        RequestedUserId = requestedUserId;
    }

    /// <summary>The thread that was addressed.</summary>
    public string ThreadId { get; }

    /// <summary>The <c>{tid}:{oid}</c> the thread is bound to; null for an unowned conversation.</summary>
    public string? ExistingUserId { get; }

    /// <summary>The <c>{tid}:{oid}</c> the conflicting request carried; null for an app-only caller.</summary>
    public string? RequestedUserId { get; }

    private static string Describe(string? userId) =>
        string.IsNullOrWhiteSpace(userId) ? "(none - app-only or unowned)" : userId;
}
