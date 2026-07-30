using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

/// <summary>
/// A failure, described well enough to act on and no more.
/// </summary>
/// <remarks>
/// <see cref="Code"/> is the part a program should branch on; <see cref="Message"/> is for a human
/// reading a log. Neither ever carries credentials, secrets, signatures, request bodies, or full
/// URLs — a diagnostic that leaks the thing it is diagnosing is worse than no diagnostic.
/// </remarks>
public sealed record LifecycleError
{
    /// <summary>
    /// A stable, machine-comparable code. Open vocabulary — an unrecognized code is preserved.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>A short human-readable description, free of sensitive material.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
