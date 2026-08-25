namespace LmStreaming.Sample.Models;

/// <summary>
/// Names one person and the role they are given on a conversation (P1 spec 8.4).
/// </summary>
public record ConversationShareRequest
{
    /// <summary>
    /// The subject to share with: the namespaced <c>{tid}:{oid}</c> of spec 3.3, not a UPN. A UPN
    /// is re-assignable and is the identifier an attacker can most easily get pointed at
    /// themselves; the object id is not.
    /// </summary>
    public required string SubjectId { get; init; }

    /// <summary>
    /// <c>viewer</c> or <c>editor</c>. Anything else is rejected rather than defaulted - a typo
    /// that quietly became "viewer" would be a silent downgrade, and one that quietly became
    /// "editor" would be a silent escalation.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>Optional expiry, Unix milliseconds. Null means the grant does not expire.</summary>
    public long? ExpiresAtUnixMs { get; init; }
}

/// <summary>One grant, as returned by the sharing routes.</summary>
public record ConversationShareResponse
{
    /// <summary>The conversation the grant is on.</summary>
    public required string ThreadId { get; init; }

    /// <summary>The subject the grant names.</summary>
    public required string SubjectId { get; init; }

    /// <summary><c>viewer</c> or <c>editor</c>.</summary>
    public required string Role { get; init; }

    /// <summary>Who created the grant.</summary>
    public required string GrantedBy { get; init; }

    /// <summary>When the grant was created, Unix milliseconds.</summary>
    public required long GrantedAtUnixMs { get; init; }

    /// <summary>When the grant expires, Unix milliseconds, or null.</summary>
    public long? ExpiresAtUnixMs { get; init; }
}
