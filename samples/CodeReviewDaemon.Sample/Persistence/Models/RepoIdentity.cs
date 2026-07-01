namespace CodeReviewDaemon.Sample.Persistence.Models;

/// <summary>
/// Canonical repository identity (plan §7). The <see cref="NormalizedKey"/> is the case-folded key
/// used for every SQLite row, ReviewBot path, idempotency marker, and branch name, while
/// <see cref="DisplayName"/> preserves the provider's original casing for display. This split is what
/// guards against <c>LmDotnetTools</c> vs <c>LmDotNetTools</c> casing drift on case-sensitive SQLite
/// keys, filesystem paths, and git branches.
/// </summary>
internal sealed record RepoIdentity
{
    /// <summary>Provider namespace, e.g. <c>github</c> or <c>azure-devops</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>GitHub owner/org, or ADO account/organization.</summary>
    public required string OrgOrOwner { get; init; }

    /// <summary>ADO project (collection); <c>null</c> for GitHub which has no project layer.</summary>
    public string? Project { get; init; }

    /// <summary>Repository name as the provider presents it (original casing preserved).</summary>
    public required string RepoName { get; init; }

    /// <summary>
    /// Provider stable repository id (e.g. GitHub node id / ADO repo GUID) when exposed, stored as
    /// <c>TEXT</c>. <c>null</c> when the provider exposes no stable id and the normalized name is the
    /// only identity available.
    /// </summary>
    public string? RepoStableId { get; init; }

    /// <summary>
    /// Case-folded, separator-joined identity used as the durable key. Built from the human identity
    /// (provider + org + project? + repo name) so two observations differing only by casing collapse
    /// to one row.
    /// </summary>
    public string NormalizedKey =>
        string.Join(
            '/',
            new[] { Provider, OrgOrOwner, Project, RepoName }
                .Where(static p => !string.IsNullOrEmpty(p))
                .Select(static p => p!.ToLowerInvariant()));

    /// <summary>Human-facing identity with original casing preserved (never used as a durable key).</summary>
    public string DisplayName =>
        Project is null ? $"{OrgOrOwner}/{RepoName}" : $"{OrgOrOwner}/{Project}/{RepoName}";
}
