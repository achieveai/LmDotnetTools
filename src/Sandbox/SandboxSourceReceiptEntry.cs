namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// One source of a preparation receipt. Git fields are null for a mutable or archive source. <see cref="Url"/> may name
/// a private repository, so <see cref="ToString"/> prints it redacted; the property itself is the gateway's value.
/// </summary>
public sealed record SandboxSourceReceiptEntry(
    string Kind,
    string Marketplace,
    string? Plugin,
    string? Url,
    string? RepoKey,
    string? Subdir,
    string? Sha
)
{
    /// <summary>Redacted rendering — never prints <see cref="Url"/>.</summary>
    public override string ToString() =>
        $"SandboxSourceReceiptEntry {{ Kind = {Kind}, Marketplace = {Marketplace}, Plugin = {Plugin}, "
        + $"Url = {(Url is null ? null : "[REDACTED]")}, RepoKey = {RepoKey}, Subdir = {Subdir}, Sha = {Sha} }}";
}
