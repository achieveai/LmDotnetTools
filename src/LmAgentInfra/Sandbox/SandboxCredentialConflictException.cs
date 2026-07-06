namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Thrown when a conversation bound to one caller's app identity (the
/// <see cref="SandboxCredential.AppId"/> that created it) is addressed by a request carrying a
/// DIFFERENT app identity. Per the Cross-Actor Resume Matrix (issue #153), a conversation is frozen
/// to its creating <c>AppId</c> for its lifetime: UI (no credential) &lt;-&gt; S2S, and S2S-A &lt;-&gt;
/// S2S-B, both conflict in either direction; only a matching <c>AppId</c> (including "both null" for
/// two plain UI reconnects) may continue the thread. The message includes both app ids for
/// diagnosability — it NEVER includes either caller's app key.
/// </summary>
public sealed class SandboxCredentialConflictException : Exception
{
    public SandboxCredentialConflictException(string threadId, string? existingAppId, string? requestedAppId)
        : base(
            $"Thread '{threadId}' is bound to app id '{DescribeAppId(existingAppId)}'; the current "
                + $"request carries app id '{DescribeAppId(requestedAppId)}'. A conversation cannot "
                + "change its owning app identity."
        )
    {
        ThreadId = threadId;
        ExistingAppId = existingAppId;
        RequestedAppId = requestedAppId;
    }

    /// <summary>The thread that was addressed.</summary>
    public string ThreadId { get; }

    /// <summary>App id the thread was created (and is still bound) under; <c>null</c> for the
    /// interactive UI default (no caller credential).</summary>
    public string? ExistingAppId { get; }

    /// <summary>App id the conflicting request carried; <c>null</c> for the interactive UI default
    /// (no caller credential).</summary>
    public string? RequestedAppId { get; }

    private static string DescribeAppId(string? appId) =>
        string.IsNullOrWhiteSpace(appId) ? "(none — interactive UI default)" : appId;
}
