using AchieveAi.LmDotnetTools.LmCore.Identity;
using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Identity;

/// <summary>
/// Applies <c>Identity:SeedTenants</c> at startup, idempotently, and only while
/// <c>Identity:Enforce</c> is false.
/// </summary>
/// <remarks>
/// <para>
/// The enforcement guard is the point of the whole class. A seed list is a configuration file, and
/// configuration files get copied between environments; in an enforcing deployment that is a path
/// by which a stale entry could silently mint a real tenant nobody provisioned. With enforcement on
/// the only way in is <c>POST /api/admin/tenants</c>, which requires the operator secret and leaves
/// an audit record.
/// </para>
/// <para>
/// Idempotency is delegated, not reimplemented: <see cref="ITenantStore.ProvisionAsync"/> already
/// refuses to overwrite, so a re-run reports <see cref="TenantProvisionOutcome.TenantIdExists"/>
/// and touches nothing. That also means a restart never rebinds an admin who has already signed in.
/// </para>
/// </remarks>
public sealed class TenantSeedHostedService : IHostedService
{
    private readonly ITenantStore _tenantStore;
    private readonly IOptions<IdentityOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantSeedHostedService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="tenantStore">Tenant registry.</param>
    /// <param name="options">Identity configuration carrying the seed list.</param>
    /// <param name="timeProvider">Clock stamped onto seeded tenants.</param>
    /// <param name="logger">Diagnostics.</param>
    public TenantSeedHostedService(
        ITenantStore tenantStore,
        IOptions<IdentityOptions> options,
        TimeProvider timeProvider,
        ILogger<TenantSeedHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(tenantStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _tenantStore = tenantStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;

        if (options.SeedTenants.Count == 0)
        {
            return;
        }

        if (options.Enforce)
        {
            _logger.LogWarning(
                "Ignoring {SeedCount} configured Identity:SeedTenants because Identity:Enforce is "
                    + "true. Provision tenants through POST /api/admin/tenants instead.",
                options.SeedTenants.Count);
            return;
        }

        foreach (var seed in options.SeedTenants)
        {
            await ApplyAsync(seed, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ApplyAsync(SeedTenantOptions seed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(seed.TenantId)
            || string.IsNullOrWhiteSpace(seed.EntraTenantId)
            || string.IsNullOrWhiteSpace(seed.FirstAdminUpn))
        {
            // Logged rather than thrown: a malformed entry must not stop the host from starting,
            // and a silent skip would leave an operator wondering why their tenant never appeared.
            _logger.LogError(
                "Skipping an Identity:SeedTenants entry for {TenantId}: TenantId, EntraTenantId and "
                    + "FirstAdminUpn are all required.",
                seed.TenantId);
            return;
        }

        var outcome = await _tenantStore
            .ProvisionAsync(
                new TenantRecord
                {
                    TenantId = seed.TenantId.Trim(),
                    EntraTenantId = seed.EntraTenantId.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(seed.DisplayName)
                        ? seed.TenantId.Trim()
                        : seed.DisplayName.Trim(),
                    Status = TenantStatus.Active,
                    CreatedAt = _timeProvider.GetUtcNow(),
                    CreatedBy = "seed",
                },
                seed.FirstAdminUpn.Trim(),
                ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Seed tenant {TenantId} ({EntraTenantId}): {Outcome}.",
            seed.TenantId,
            seed.EntraTenantId,
            outcome);
    }
}
