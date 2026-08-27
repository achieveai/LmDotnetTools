using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Tests.Identity;

/// <summary>
/// Pins <see cref="TenantSeedHostedService"/> against the two properties issue #301 asks of the
/// startup seed: it is idempotent, and it is INERT once the deployment is enforcing.
/// </summary>
/// <remarks>
/// The enforcement guard is the clause with teeth. A seed list lives in a configuration file, and
/// configuration files get copied between environments; without the guard, a stale entry carried
/// into an enforcing deployment would silently mint a real tenant that no operator provisioned -
/// which is precisely the "explicitly provisioned" rule being defeated by its own convenience
/// feature.
/// </remarks>
public sealed class TenantSeedHostedServiceTests
{
    private const string EntraTenant = "11111111-1111-1111-1111-111111111111";

    private readonly RecordingTenantStore _store = new();

    /// <summary>
    /// A capturing logger rather than <c>NullLogger</c>, because the seed fix has two halves -
    /// survive the failure, and TELL somebody - and a null logger asserts only the first. With one,
    /// deleting the <c>LogError</c> from the catch leaves a bare silent swallow that every test here
    /// still passes, which is exactly the "why did my tenant never appear" mystery the malformed-entry
    /// skip path is documented to prevent.
    /// <para>
    /// Fully qualified deliberately: <c>PrincipalFactoryTests</c> declares its OWN internal
    /// <c>CapturingLogger&lt;T&gt;</c> in this same namespace, which wins over any imported one - and
    /// that one discards the exception, so it cannot see the half of the log line this asserts.
    /// </para>
    /// </summary>
    private readonly AchieveAi.LmDotnetTools.LmTestUtils.Logging.CapturingLogger<TenantSeedHostedService>
        _logger = new();

    private static IdentityOptions OptionsWith(bool enforce, params SeedTenantOptions[] seeds) =>
        new() { Enforce = enforce, SeedTenants = seeds };

    private TenantSeedHostedService CreateService(IdentityOptions options) =>
        new(_store, Options.Create(options), TimeProvider.System, _logger);

    private static SeedTenantOptions ValidSeed() =>
        new()
        {
            TenantId = "tnt_dev",
            EntraTenantId = EntraTenant,
            DisplayName = "Dev Tenant",
            FirstAdminUpn = "ada@dev.example",
        };

    [Fact]
    public async Task WithEnforcementOff_AConfiguredSeed_IsApplied()
    {
        var service = CreateService(OptionsWith(enforce: false, ValidSeed()));

        await service.StartAsync(CancellationToken.None);

        _ = _store.Provisioned.Should().ContainSingle().Which.TenantId.Should().Be("tnt_dev");
    }

    [Fact]
    public async Task WithEnforcementOn_AConfiguredSeed_IsIgnoredEntirely()
    {
        var service = CreateService(OptionsWith(enforce: true, ValidSeed()));

        await service.StartAsync(CancellationToken.None);

        // Not "applied and then rejected" - never attempted. In an enforcing deployment the only
        // way a tenant comes into existence is POST /api/admin/tenants, which needs the operator
        // secret and leaves an audit record.
        _ = _store.Provisioned.Should().BeEmpty();
    }

    [Fact]
    public async Task ReRunningTheSeed_ChangesNothing()
    {
        var service = CreateService(OptionsWith(enforce: false, ValidSeed()));

        await service.StartAsync(CancellationToken.None);

        // A restart must not re-provision, because re-provisioning would reset the first-admin row
        // and unbind an admin who has already signed in.
        _store.NextOutcome = TenantProvisionOutcome.TenantIdExists;
        await service.StartAsync(CancellationToken.None);

        _ = _store.Provisioned.Should().ContainSingle();
    }

    [Theory]
    [InlineData(null, EntraTenant, "ada@dev.example")]
    [InlineData("tnt_dev", null, "ada@dev.example")]
    [InlineData("tnt_dev", EntraTenant, null)]
    public async Task AnIncompleteSeedEntry_IsSkippedWithoutStoppingStartup(
        string? tenantId,
        string? entraTenantId,
        string? firstAdminUpn)
    {
        var service = CreateService(OptionsWith(
            enforce: false,
            new SeedTenantOptions
            {
                TenantId = tenantId,
                EntraTenantId = entraTenantId,
                DisplayName = "Dev Tenant",
                FirstAdminUpn = firstAdminUpn,
            }));

        // Skipped, not thrown: a malformed entry must not stop the host from starting. A tenant
        // with no named admin has nobody who could ever administer it, so a partial row would be
        // worse than none.
        await service.StartAsync(CancellationToken.None);

        _ = _store.Provisioned.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEmptySeedList_TouchesTheStoreNotAtAll()
    {
        var service = CreateService(OptionsWith(enforce: false));

        await service.StartAsync(CancellationToken.None);

        _ = _store.Provisioned.Should().BeEmpty();
    }

    [Fact]
    public async Task AStoreFailure_DoesNotStopTheHostFromStarting()
    {
        // #350 item 6. The malformed-entry path above was already logged-and-skipped on the stated
        // ground that "a malformed entry must not stop the host from starting" - but the store call
        // itself sat outside any try, so a transient SQLite lock propagated out of StartAsync and
        // aborted the host. The same seed feature, on the same startup path, had one failure mode it
        // survived and one it did not. A convenience feature that can prevent boot is a liability
        // whichever way the failure arrives.
        _store.ProvisionFailure = _ => new InvalidOperationException("database is locked");
        var service = CreateService(OptionsWith(enforce: false, ValidSeed()));

        await service.StartAsync(CancellationToken.None);

        _ = _store.Provisioned.Should().BeEmpty();

        // The other half of the fix, and the half nothing else here would notice going missing.
        // Surviving a failure silently is the failure mode the malformed-entry path exists to
        // prevent, so the catch has to leave a record naming what went wrong.
        //
        // Asserted on the logged EXCEPTION rather than on the rendered message: the default MEL
        // formatter renders the template alone and drops the exception, so a call site that stopped
        // passing `ex` would produce a byte-identical string and a message assertion would not
        // notice - see CapturingLogger.CountAtLevelWithExceptionText.
        _ = _logger.CountAtLevelWithExceptionText(LogLevel.Error, "database is locked").Should().Be(1);
    }

    [Fact]
    public async Task AStoreFailureOnOneEntry_StillAppliesTheRest()
    {
        // The clause that distinguishes a catch INSIDE the loop from a catch around it. Both stop
        // the host aborting and would pass the test above; only one still provisions the entries
        // after the failing one. Silently dropping the tail of an operator's seed list is the same
        // "your tenant never appeared" mystery the skip path exists to prevent.
        _store.ProvisionFailure = tenant =>
            tenant.TenantId == "tnt_first" ? new InvalidOperationException("database is locked") : null;

        var service = CreateService(
            OptionsWith(
                enforce: false,
                SeedFor("tnt_first"),
                SeedFor("tnt_second")));

        await service.StartAsync(CancellationToken.None);

        _ = _store.Provisioned.Should().ContainSingle().Which.TenantId.Should().Be("tnt_second");
    }

    [Fact]
    public async Task ACancelledStartup_StillStops()
    {
        // The catch must not be broad enough to swallow shutdown. StartAsync's token is cancelled
        // when the host is being torn down, and continuing to walk the seed list against a store
        // that is going away turns an orderly shutdown into a run of failures.
        //
        // The token is genuinely cancelled and the OCE genuinely carries it, so what runs is the
        // case the name describes. Passing CancellationToken.None and throwing a bare OCE would
        // exercise the uncancelled-OCE case instead - which the type-based filter also excludes,
        // but which is a different claim from this one.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _store.ProvisionFailure = _ => new OperationCanceledException(cts.Token);
        var service = CreateService(OptionsWith(enforce: false, ValidSeed()));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.StartAsync(cts.Token));
    }

    private static SeedTenantOptions SeedFor(string tenantId) =>
        new()
        {
            TenantId = tenantId,
            EntraTenantId = EntraTenant,
            DisplayName = tenantId,
            FirstAdminUpn = "ada@dev.example",
        };
}
