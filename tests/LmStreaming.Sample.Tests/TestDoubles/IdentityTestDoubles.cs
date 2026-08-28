using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.Tests.Identity;

namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>An <see cref="IPrincipalAccessor"/> whose answer is fixed at construction.</summary>
internal sealed class StubPrincipalAccessor(Principal? principal) : IPrincipalAccessor
{
    public Principal? Current { get; set; } = principal;
}

/// <summary>
/// An in-memory <see cref="IResourceGrantStore"/>, so a policy test can pin the grantee branch
/// without a database.
/// </summary>
internal sealed class InMemoryResourceGrantStore : IResourceGrantStore
{
    private readonly List<ResourceGrant> _grants = [];

    /// <summary>Every grant currently held, in insertion order.</summary>
    public IReadOnlyList<ResourceGrant> All => _grants;

    public Task<GrantRole?> FindGrantAsync(
        string tenantId,
        ResourceRef resource,
        string subjectId,
        DateTimeOffset now,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            Unexpired(tenantId, resource, now)
                .Where(g => string.Equals(g.SubjectId, subjectId, StringComparison.Ordinal))
                .Select(g => (GrantRole?)g.Role)
                .FirstOrDefault()
        );

    public Task<IReadOnlyList<string>> ListGrantedResourceIdsAsync(
        string tenantId,
        string subjectId,
        string resourceType,
        DateTimeOffset now,
        CancellationToken ct = default
    ) =>
        Task.FromResult<IReadOnlyList<string>>([
            .. _grants
                .Where(g =>
                    string.Equals(g.TenantId, tenantId, StringComparison.Ordinal)
                    && string.Equals(g.Resource.Type, resourceType, StringComparison.Ordinal)
                    && string.Equals(g.SubjectId, subjectId, StringComparison.Ordinal)
                    && (g.ExpiresAt is null || g.ExpiresAt > now)
                )
                .Select(g => g.Resource.Id),
        ]);

    public Task<IReadOnlyList<ResourceGrant>> ListGrantsForResourceAsync(
        string tenantId,
        ResourceRef resource,
        CancellationToken ct = default
    ) =>
        Task.FromResult<IReadOnlyList<ResourceGrant>>([
            .. _grants.Where(g =>
                string.Equals(g.TenantId, tenantId, StringComparison.Ordinal) && g.Resource == resource
            ),
        ]);

    public Task GrantAsync(ResourceGrant grant, CancellationToken ct = default)
    {
        _ = _grants.RemoveAll(g =>
            string.Equals(g.TenantId, grant.TenantId, StringComparison.Ordinal)
            && g.Resource == grant.Resource
            && string.Equals(g.SubjectId, grant.SubjectId, StringComparison.Ordinal)
        );
        _grants.Add(grant);
        return Task.CompletedTask;
    }

    public Task<bool> RevokeAsync(
        string tenantId,
        ResourceRef resource,
        string subjectId,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            _grants.RemoveAll(g =>
                string.Equals(g.TenantId, tenantId, StringComparison.Ordinal)
                && g.Resource == resource
                && string.Equals(g.SubjectId, subjectId, StringComparison.Ordinal)
            ) > 0
        );

    public Task<bool> HasAnyGrantAsync(
        string tenantId,
        ResourceRef resource,
        DateTimeOffset now,
        CancellationToken ct = default
    ) => Task.FromResult(Unexpired(tenantId, resource, now).Any());

    private IEnumerable<ResourceGrant> Unexpired(string tenantId, ResourceRef resource, DateTimeOffset now) =>
        _grants.Where(g =>
            string.Equals(g.TenantId, tenantId, StringComparison.Ordinal)
            && g.Resource == resource
            && (g.ExpiresAt is null || g.ExpiresAt > now)
        );
}

/// <summary>
/// A forwarding <see cref="IResourceGrantStore"/> that counts grant lookups, so a test can assert
/// the SHAPE of the work a refusal does rather than its wall-clock duration (#389).
/// </summary>
/// <remarks>
/// Counting is the only honest way to pin this. A timing assertion on a single grant lookup is
/// dominated by scheduling noise on any machine, so it would either be flaky or - far more likely -
/// be widened until it passes for both shapes and proves nothing at all.
/// </remarks>
/// <param name="inner">The real store every call is forwarded to.</param>
internal sealed class CountingResourceGrantStore(IResourceGrantStore inner) : IResourceGrantStore
{
    private int _findGrantCalls;

    /// <summary>How many times <see cref="FindGrantAsync"/> has been called.</summary>
    public int FindGrantCallCount => Volatile.Read(ref _findGrantCalls);

    public Task<GrantRole?> FindGrantAsync(
        string tenantId,
        ResourceRef resource,
        string subjectId,
        DateTimeOffset now,
        CancellationToken ct = default
    )
    {
        _ = Interlocked.Increment(ref _findGrantCalls);
        return inner.FindGrantAsync(tenantId, resource, subjectId, now, ct);
    }

    public Task<IReadOnlyList<string>> ListGrantedResourceIdsAsync(
        string tenantId,
        string subjectId,
        string resourceType,
        DateTimeOffset now,
        CancellationToken ct = default
    ) => inner.ListGrantedResourceIdsAsync(tenantId, subjectId, resourceType, now, ct);

    public Task<IReadOnlyList<ResourceGrant>> ListGrantsForResourceAsync(
        string tenantId,
        ResourceRef resource,
        CancellationToken ct = default
    ) => inner.ListGrantsForResourceAsync(tenantId, resource, ct);

    public Task GrantAsync(ResourceGrant grant, CancellationToken ct = default) => inner.GrantAsync(grant, ct);

    public Task<bool> RevokeAsync(
        string tenantId,
        ResourceRef resource,
        string subjectId,
        CancellationToken ct = default
    ) => inner.RevokeAsync(tenantId, resource, subjectId, ct);

    public Task<bool> HasAnyGrantAsync(
        string tenantId,
        ResourceRef resource,
        DateTimeOffset now,
        CancellationToken ct = default
    ) => inner.HasAnyGrantAsync(tenantId, resource, now, ct);
}

/// <summary>Builds <see cref="ConversationAuthorizer"/> instances for controller tests.</summary>
internal static class TestAuthorizers
{
    /// <summary>
    /// An authorizer with enforcement OFF - what every test predating #302 runs under, and what
    /// keeps their expectations unchanged: with the gate off the policy short-circuits to
    /// <c>enforcement_disabled</c> before it looks at tenant, owner or role.
    /// </summary>
    /// <param name="principal">
    /// The signed-in caller, or null. Off-arm decisions never consult it, but a test can supply a
    /// NON-owner to prove the off-arm is the enforcement-off path and not "the principal happens to
    /// own the row".
    /// </param>
    public static ConversationAuthorizer Disabled(Principal? principal = null) => Create(enforce: false, principal);

    /// <summary>An authorizer with enforcement ON, acting as <paramref name="principal"/>.</summary>
    /// <param name="principal">The signed-in caller, or null for an unauthenticated request.</param>
    /// <param name="grants">Grant registry, or null for an empty one.</param>
    /// <param name="audit">Audit sink, or null to discard.</param>
    public static ConversationAuthorizer Enforcing(
        Principal? principal,
        IResourceGrantStore? grants = null,
        IAuditSink? audit = null
    ) => Create(enforce: true, principal, grants, audit);

    private static ConversationAuthorizer Create(
        bool enforce,
        Principal? principal,
        IResourceGrantStore? grants = null,
        IAuditSink? audit = null
    )
    {
        var gate = new StaticEnforcementGate(enforce);
        var grantStore = grants ?? new InMemoryResourceGrantStore();
        var sink = audit ?? new RecordingAuditSink();

        return new ConversationAuthorizer(
            new StubPrincipalAccessor(principal),
            new ResourceAccessPolicy(grantStore, sink, gate, TimeProvider.System),
            grantStore,
            gate,
            TimeProvider.System
        );
    }
}
