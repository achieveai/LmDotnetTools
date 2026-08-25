using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Identity;
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

    private static IdentityOptions OptionsWith(bool enforce, params SeedTenantOptions[] seeds) =>
        new() { Enforce = enforce, SeedTenants = seeds };

    private TenantSeedHostedService CreateService(IdentityOptions options) =>
        new(_store, Options.Create(options), TimeProvider.System, NullLogger<TenantSeedHostedService>.Instance);

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
}
