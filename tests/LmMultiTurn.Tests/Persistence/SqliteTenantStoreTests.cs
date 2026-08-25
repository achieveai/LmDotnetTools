using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Pins <see cref="SqliteTenantStore"/> against the explicit-provisioning rules of P1 spec 4.4 and
/// 8.2: a tenant is only ever created deliberately, and a named first admin binds to a durable
/// user id exactly once.
/// </summary>
public sealed class SqliteTenantStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private string _databasePath = null!;
    private SqliteConnectionFactory _factory = null!;
    private SqliteTenantStore _store = null!;

    public Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"tenants_{Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory(_databasePath);
        _store = new SqliteTenantStore(_factory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(_databasePath + suffix))
                {
                    File.Delete(_databasePath + suffix);
                }
            }
            catch (IOException)
            {
                // A leaked temp file is not a test failure.
            }
        }
    }

    private static TenantRecord Tenant(
        string tenantId = "tnt_acme",
        string? entraTenantId = "entra-acme",
        TenantStatus status = TenantStatus.Active) =>
        new()
        {
            TenantId = tenantId,
            EntraTenantId = entraTenantId,
            DisplayName = "Acme Corp",
            Status = status,
            CreatedAt = T0,
            CreatedBy = "operator",
        };

    /// <summary>Counts tenant rows straight from SQL, so a test can assert on rows the store never returned.</summary>
    private async Task<long> CountTenantsAsync()
    {
        await using var connection = await _factory.GetConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tenants;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(string? UserId, long? BoundAt)> ReadAdminRowAsync(string tenantId, string upn)
    {
        await using var connection = await _factory.GetConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT user_id, bound_at FROM tenant_admins WHERE tenant_id = $t AND upn = $u;";
        _ = command.Parameters.AddWithValue("$t", tenantId);
        _ = command.Parameters.AddWithValue("$u", upn);
        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return (null, null);
        }

        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    [Fact]
    public async Task Provision_CreatesTheTenantAndItsNamedFirstAdmin()
    {
        var outcome = await _store.ProvisionAsync(Tenant(), "Dana@Acme.Example");

        outcome.Should().Be(TenantProvisionOutcome.Created);

        var found = await _store.FindByEntraTenantIdAsync("entra-acme");
        found.Should().NotBeNull();
        found!.TenantId.Should().Be("tnt_acme");
        found.DisplayName.Should().Be("Acme Corp");
        found.Status.Should().Be(TenantStatus.Active);
        found.CreatedAt.Should().Be(T0);
        found.CreatedBy.Should().Be("operator");

        var (userId, boundAt) = await ReadAdminRowAsync("tnt_acme", "dana@acme.example");
        userId.Should().BeNull("the admin is named before they have ever signed in");
        boundAt.Should().BeNull();
    }

    [Fact]
    public async Task FindByEntraTenantId_ReturnsNullForAnUnknownDirectory_AndCreatesNothing()
    {
        await _store.ProvisionAsync(Tenant(), "dana@acme.example");
        var before = await CountTenantsAsync();

        var found = await _store.FindByEntraTenantIdAsync("entra-stranger");

        found.Should().BeNull();
        (await CountTenantsAsync()).Should().Be(before, "a lookup must never auto-create a tenant");
    }

    [Fact]
    public async Task FindByEntraTenantId_ReturnsASuspendedTenant_SoTheCallerCanTellItApartFromUnknown()
    {
        await _store.ProvisionAsync(
            Tenant(tenantId: "tnt_lapsed", entraTenantId: "entra-lapsed", status: TenantStatus.Suspended),
            "dana@lapsed.example");

        var found = await _store.FindByEntraTenantIdAsync("entra-lapsed");

        found.Should().NotBeNull();
        found!.Status.Should().Be(TenantStatus.Suspended);
    }

    [Fact]
    public async Task Provision_RefusesADuplicateInternalId_AndWritesNothing()
    {
        await _store.ProvisionAsync(Tenant(), "dana@acme.example");
        var before = await CountTenantsAsync();

        var outcome = await _store.ProvisionAsync(
            Tenant(entraTenantId: "entra-different"), "someone.else@acme.example");

        outcome.Should().Be(TenantProvisionOutcome.TenantIdExists);
        (await CountTenantsAsync()).Should().Be(before);
        (await ReadAdminRowAsync("tnt_acme", "someone.else@acme.example")).UserId.Should().BeNull();

        await using var connection = await _factory.GetConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tenant_admins WHERE tenant_id = 'tnt_acme';";
        Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(1, "the refused call must not leave a second admin row behind");
    }

    [Fact]
    public async Task Provision_RefusesASecondTenantClaimingTheSameEntraDirectory_AndWritesNothing()
    {
        await _store.ProvisionAsync(Tenant(), "dana@acme.example");
        var before = await CountTenantsAsync();

        var outcome = await _store.ProvisionAsync(
            Tenant(tenantId: "tnt_impostor"), "attacker@acme.example");

        outcome.Should().Be(TenantProvisionOutcome.EntraTenantIdClaimed);
        (await CountTenantsAsync()).Should().Be(before);
        (await _store.FindByTenantIdAsync("tnt_impostor")).Should().BeNull();
    }

    [Fact]
    public async Task TryBindFirstAdmin_BindsOnTheFirstSignIn_AndNeverRebindsAfterwards()
    {
        await _store.ProvisionAsync(Tenant(), "dana@acme.example");

        var firstBind = await _store.TryBindFirstAdminAsync(
            "tnt_acme", "dana@acme.example", "entra-acme:oid-dana", T0);

        firstBind.Should().BeTrue();
        var afterFirst = await ReadAdminRowAsync("tnt_acme", "dana@acme.example");
        afterFirst.UserId.Should().Be("entra-acme:oid-dana");
        afterFirst.BoundAt.Should().Be(T0.ToUnixTimeMilliseconds());

        // A second sign-in, at a different time and even claiming a different oid, must change
        // nothing: the UPN is consulted exactly once, because it is mutable and re-assignable.
        var secondBind = await _store.TryBindFirstAdminAsync(
            "tnt_acme", "dana@acme.example", "entra-acme:oid-someone-else", T1);

        secondBind.Should().BeFalse();
        var afterSecond = await ReadAdminRowAsync("tnt_acme", "dana@acme.example");
        afterSecond.UserId.Should().Be("entra-acme:oid-dana", "the bound id must not be reassigned");
        afterSecond.BoundAt.Should().Be(T0.ToUnixTimeMilliseconds(), "bound_at must still be the first sign-in");
    }

    [Fact]
    public async Task TryBindFirstAdmin_MatchesTheUpnCaseInsensitively()
    {
        await _store.ProvisionAsync(Tenant(), "dana@acme.example");

        var bound = await _store.TryBindFirstAdminAsync(
            "tnt_acme", "DANA@Acme.Example", "entra-acme:oid-dana", T0);

        bound.Should().BeTrue();
    }

    [Fact]
    public async Task TryBindFirstAdmin_DoesNothingForAUserWhoWasNeverNamed()
    {
        await _store.ProvisionAsync(Tenant(), "dana@acme.example");

        var bound = await _store.TryBindFirstAdminAsync(
            "tnt_acme", "random@acme.example", "entra-acme:oid-random", T0);

        bound.Should().BeFalse();
        (await _store.IsTenantAdminAsync("tnt_acme", "entra-acme:oid-random")).Should().BeFalse();
    }

    [Fact]
    public async Task IsTenantAdmin_IsTrueOnlyAfterBinding_AndOnlyWithinThatTenant()
    {
        await _store.ProvisionAsync(Tenant(), "dana@acme.example");
        await _store.ProvisionAsync(
            Tenant(tenantId: "tnt_other", entraTenantId: "entra-other"), "dana@acme.example");

        (await _store.IsTenantAdminAsync("tnt_acme", "entra-acme:oid-dana"))
            .Should().BeFalse("nothing is bound yet");

        _ = await _store.TryBindFirstAdminAsync(
            "tnt_acme", "dana@acme.example", "entra-acme:oid-dana", T0);

        (await _store.IsTenantAdminAsync("tnt_acme", "entra-acme:oid-dana")).Should().BeTrue();
        (await _store.IsTenantAdminAsync("tnt_other", "entra-acme:oid-dana"))
            .Should().BeFalse("a tenant admin is an admin of exactly one tenant");
    }
}
