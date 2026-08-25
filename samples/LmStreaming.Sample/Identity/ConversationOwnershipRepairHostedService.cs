using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Identity;

/// <summary>
/// Thrown when <c>Identity:LegacyTenantId</c> names a tenant that is not the quarantine tenant.
/// </summary>
/// <remarks>
/// Stamping unclaimed conversations with a REAL tenant's id would make them readable by that
/// customer's admins immediately. The failure mode is a configuration typo, so this fails the
/// startup loudly instead of writing anything - an operator who sees the code picks a different id,
/// and nothing has been written.
/// </remarks>
public sealed class LegacyTenantIdCollisionException : Exception
{
    /// <summary>Stable code an operator greps for.</summary>
    public const string Code = "legacy_tenant_id_collision";

    /// <summary>Creates the exception.</summary>
    /// <param name="tenantId">The configured legacy tenant id.</param>
    public LegacyTenantIdCollisionException(string tenantId)
        : base(
            $"{Code}: Identity:LegacyTenantId is '{tenantId}', which already names a tenant that is "
                + "not the quarantine tenant. Choose an unused id. Nothing was written.") =>
        TenantId = tenantId;

    /// <summary>The configured legacy tenant id.</summary>
    public string TenantId { get; }
}

/// <summary>
/// The startup repair of P1 spec 8.5.4: ensure the quarantine tenant exists, then stamp every
/// conversation that has no tenant with it.
/// </summary>
/// <remarks>
/// <para>
/// A REPAIR, not a migration step. A build rolled back to before this slice does not know about
/// <c>tenant_id</c>, so every conversation it creates is unstamped, and rolling forward would not
/// fix them: the schema version is already past the step that would have, and <c>adopt-legacy</c>
/// only selects rows already stamped with the quarantine tenant. Those rows would be reachable by
/// nobody the moment enforcement was switched on - invisible, un-adoptable, and indistinguishable
/// from legacy rows without being treated as any.
/// </para>
/// <para>
/// The collision guard runs with it, every time, because a recurring update cannot be protected by
/// a one-time check.
/// </para>
/// <para>
/// It also normalizes stored Entra directory ids (#347), for the same rollback reason: a downgraded
/// build writes mixed-case ids again, and a schema-versioned repair would never revisit them.
/// </para>
/// </remarks>
public sealed class ConversationOwnershipRepairHostedService : IHostedService
{
    private readonly ITenantStore _tenantStore;
    private readonly IConversationStore _conversationStore;
    private readonly IOptions<IdentityOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationOwnershipRepairHostedService> _logger;

    /// <summary>Creates the hosted service.</summary>
    /// <param name="tenantStore">Tenant registry holding the quarantine row.</param>
    /// <param name="conversationStore">Conversation store whose rows are stamped.</param>
    /// <param name="options">Identity configuration.</param>
    /// <param name="timeProvider">Clock stamped onto a created quarantine row.</param>
    /// <param name="logger">Diagnostics.</param>
    public ConversationOwnershipRepairHostedService(
        ITenantStore tenantStore,
        IConversationStore conversationStore,
        IOptions<IdentityOptions> options,
        TimeProvider timeProvider,
        ILogger<ConversationOwnershipRepairHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(tenantStore);
        ArgumentNullException.ThrowIfNull(conversationStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _tenantStore = tenantStore;
        _conversationStore = conversationStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var legacyTenantId = _options.Value.LegacyTenantId;

        if (string.IsNullOrWhiteSpace(legacyTenantId))
        {
            throw new InvalidOperationException(
                "Identity:LegacyTenantId must not be blank; it is the id every unclaimed "
                    + "conversation is stamped with.");
        }

        // Resolved ONCE, before anything is written, and the same value is used for the quarantine
        // row and for the stamp. A literal in one place and a configured value in the other would
        // stamp every conversation with a tenant that does not exist.
        if (!await _tenantStore
                .TryEnsureQuarantineTenantAsync(legacyTenantId, _timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false))
        {
            throw new LegacyTenantIdCollisionException(legacyTenantId);
        }

        var normalization = await _tenantStore
            .NormalizeEntraTenantIdsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (normalization.Rewritten > 0)
        {
            _logger.LogInformation(
                "Normalized {NormalizedCount} stored Entra directory id(s) to lower case.",
                normalization.Rewritten);
        }

        if (normalization.SkippedCollisions > 0)
        {
            // Not an error the repair can fix - it deliberately leaves the row rather than failing
            // the boot - but not something to swallow either. Each skipped row is a directory two
            // tenants claim in different shapes, and it is unreachable until an operator picks which
            // one keeps it. Logged as a warning so that state is visible after a boot instead of
            // only inferable from a rewritten count that came back lower than expected.
            _logger.LogWarning(
                "Left {SkippedCount} stored Entra directory id(s) un-normalized: each would collide "
                    + "with a row that already owns its canonical form. Those tenants are unreachable "
                    + "until the duplicate directory registration is resolved.",
                normalization.SkippedCollisions);
        }

        if (_conversationStore is not IConversationOwnershipStore ownership)
        {
            // Fail closed while enforcing: a store whose rows cannot be stamped is a store whose
            // rows carry no tenant, and under enforcement every one of those is invisible to
            // everybody. Better to refuse the boot than to serve a silently empty product.
            if (_options.Value.Enforce)
            {
                throw new InvalidOperationException(
                    $"Identity:Enforce is on but the registered conversation store "
                        + $"({_conversationStore.GetType().Name}) does not implement "
                        + $"{nameof(IConversationOwnershipStore)}, so unclaimed conversations cannot "
                        + "be stamped with the quarantine tenant.");
            }

            _logger.LogWarning(
                "Conversation store {StoreType} does not implement {Interface}; the legacy tenant "
                    + "stamp was skipped. This is only safe while Identity:Enforce is false.",
                _conversationStore.GetType().Name,
                nameof(IConversationOwnershipStore));
            return;
        }

        var stamped = await ownership
            .StampUnownedThreadsAsync(legacyTenantId, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Legacy tenant repair stamped {StampedCount} conversation(s) with quarantine tenant {TenantId}.",
            stamped,
            legacyTenantId);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
