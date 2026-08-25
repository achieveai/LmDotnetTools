namespace AchieveAi.LmDotnetTools.LmCore.Identity;

/// <summary>
/// The closed role vocabulary a grant may confer. Spec 8.4 closes it with a SQL
/// <c>CHECK</c> constraint as well, so a grant conferring something else cannot sit in the table
/// waiting for a policy bug to honour it.
/// </summary>
public enum GrantRole
{
    /// <summary>Confers <see cref="AccessAction.Read"/> and <see cref="AccessAction.Use"/>.</summary>
    Viewer = 0,

    /// <summary>Confers <see cref="GrantRole.Viewer"/> plus <see cref="AccessAction.Write"/>.</summary>
    Editor = 1,
}

/// <summary>One named-user grant on one resource.</summary>
public sealed record ResourceGrant
{
    /// <summary>Owning tenant. Part of the primary key, so a grant never crosses a tenancy boundary.</summary>
    public required string TenantId { get; init; }

    /// <summary>The resource the grant is on.</summary>
    public required ResourceRef Resource { get; init; }

    /// <summary>The grantee's durable <c>{tid}:{oid}</c> id.</summary>
    public required string SubjectId { get; init; }

    /// <summary>What the grant confers.</summary>
    public required GrantRole Role { get; init; }

    /// <summary>The <c>{tid}:{oid}</c> of the party that issued the grant.</summary>
    public required string GrantedBy { get; init; }

    /// <summary>When the grant was issued.</summary>
    public required DateTimeOffset GrantedAt { get; init; }

    /// <summary>When the grant stops conferring anything. Null means no expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Reads and writes <c>resource_grants</c> (spec 8.4). One table serves conversations, workspaces
/// and modes, so the three resource slices share the sharing surface, the policy code and the
/// audit shape rather than growing three near-identical mechanisms.
/// </summary>
public interface IResourceGrantStore
{
    /// <summary>
    /// The role an unexpired grant confers on one resource, or null when the subject holds none.
    /// </summary>
    /// <param name="tenantId">Our internal tenant id.</param>
    /// <param name="resource">The resource being addressed.</param>
    /// <param name="subjectId">The grantee's durable <c>{tid}:{oid}</c> id.</param>
    /// <param name="now">Evaluation time, used to exclude expired grants.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GrantRole?> FindGrantAsync(
        string tenantId,
        ResourceRef resource,
        string subjectId,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// The ids of every resource of one type on which the subject holds an unexpired grant. This is
    /// what the listing filter of spec 7.5 binds as its grant branch.
    /// </summary>
    /// <param name="tenantId">Our internal tenant id.</param>
    /// <param name="subjectId">The grantee's durable <c>{tid}:{oid}</c> id.</param>
    /// <param name="resourceType">Resource type, e.g. <c>conversation</c>.</param>
    /// <param name="now">Evaluation time, used to exclude expired grants.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<string>> ListGrantedResourceIdsAsync(
        string tenantId,
        string subjectId,
        string resourceType,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>Every grant currently recorded against one resource, expired ones included.</summary>
    /// <param name="tenantId">Our internal tenant id.</param>
    /// <param name="resource">The resource being addressed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ResourceGrant>> ListGrantsForResourceAsync(
        string tenantId,
        ResourceRef resource,
        CancellationToken ct = default);

    /// <summary>Creates or replaces one grant.</summary>
    /// <param name="grant">The grant to write.</param>
    /// <param name="ct">Cancellation token.</param>
    Task GrantAsync(ResourceGrant grant, CancellationToken ct = default);

    /// <summary>Removes one grant. Returns false when no grant was there to remove.</summary>
    /// <param name="tenantId">Our internal tenant id.</param>
    /// <param name="resource">The resource being addressed.</param>
    /// <param name="subjectId">The grantee's durable <c>{tid}:{oid}</c> id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> RevokeAsync(
        string tenantId,
        ResourceRef resource,
        string subjectId,
        CancellationToken ct = default);

    /// <summary>Whether any unexpired grant still names the resource.</summary>
    /// <remarks>
    /// On the interface, not just on the SQLite class, because it is what decides whether a revoke
    /// returns a conversation to <see cref="Visibility.Private"/>. A caller that had to type-test
    /// for a concrete store in order to ask would silently skip that transition against any other
    /// implementation, and <c>Shared</c> would outlive the last grant.
    /// </remarks>
    /// <param name="tenantId">Our internal tenant id.</param>
    /// <param name="resource">The resource being addressed.</param>
    /// <param name="now">Evaluation time; a grant that expired at or before it does not count.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> HasAnyGrantAsync(
        string tenantId,
        ResourceRef resource,
        DateTimeOffset now,
        CancellationToken ct = default);
}
