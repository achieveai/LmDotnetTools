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
        TenantStatus status = TenantStatus.Active
    ) =>
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
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(string? UserId, long? BoundAt)> ReadAdminRowAsync(string tenantId, string upn)
    {
        await using var connection = await _factory.GetConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, bound_at FROM tenant_admins WHERE tenant_id = $t AND upn = $u;";
        _ = command.Parameters.AddWithValue("$t", tenantId);
        _ = command.Parameters.AddWithValue("$u", upn);
        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return (null, null);
        }

        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
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
            "dana@lapsed.example"
        );

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
            Tenant(entraTenantId: "entra-different"),
            "someone.else@acme.example"
        );

        outcome.Should().Be(TenantProvisionOutcome.TenantIdExists);
        (await CountTenantsAsync()).Should().Be(before);
        (await ReadAdminRowAsync("tnt_acme", "someone.else@acme.example")).UserId.Should().BeNull();

        await using var connection = await _factory.GetConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tenant_admins WHERE tenant_id = 'tnt_acme';";
        Convert
            .ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            .Should()
            .Be(1, "the refused call must not leave a second admin row behind");
    }

    [Fact]
    public async Task Provision_RefusesASecondTenantClaimingTheSameEntraDirectory_AndWritesNothing()
    {
        await _store.ProvisionAsync(Tenant(), "dana@acme.example");
        var before = await CountTenantsAsync();

        var outcome = await _store.ProvisionAsync(Tenant(tenantId: "tnt_impostor"), "attacker@acme.example");

        outcome.Should().Be(TenantProvisionOutcome.EntraTenantIdClaimed);
        (await CountTenantsAsync()).Should().Be(before);
        (await _store.FindByTenantIdAsync("tnt_impostor")).Should().BeNull();
    }

    [Fact]
    public async Task TryBindFirstAdmin_BindsOnTheFirstSignIn_AndNeverRebindsAfterwards()
    {
        await _store.ProvisionAsync(Tenant(), "dana@acme.example");

        var firstBind = await _store.TryBindFirstAdminAsync("tnt_acme", "dana@acme.example", "entra-acme:oid-dana", T0);

        firstBind.Should().BeTrue();
        var afterFirst = await ReadAdminRowAsync("tnt_acme", "dana@acme.example");
        afterFirst.UserId.Should().Be("entra-acme:oid-dana");
        afterFirst.BoundAt.Should().Be(T0.ToUnixTimeMilliseconds());

        // A second sign-in, at a different time and even claiming a different oid, must change
        // nothing: the UPN is consulted exactly once, because it is mutable and re-assignable.
        var secondBind = await _store.TryBindFirstAdminAsync(
            "tnt_acme",
            "dana@acme.example",
            "entra-acme:oid-someone-else",
            T1
        );

        secondBind.Should().BeFalse();
        var afterSecond = await ReadAdminRowAsync("tnt_acme", "dana@acme.example");
        afterSecond.UserId.Should().Be("entra-acme:oid-dana", "the bound id must not be reassigned");
        afterSecond.BoundAt.Should().Be(T0.ToUnixTimeMilliseconds(), "bound_at must still be the first sign-in");
    }

    [Fact]
    public async Task TryBindFirstAdmin_MatchesTheUpnCaseInsensitively()
    {
        await _store.ProvisionAsync(Tenant(), "dana@acme.example");

        var bound = await _store.TryBindFirstAdminAsync("tnt_acme", "DANA@Acme.Example", "entra-acme:oid-dana", T0);

        bound.Should().BeTrue();
    }

    [Fact]
    public async Task TryBindFirstAdmin_DoesNothingForAUserWhoWasNeverNamed()
    {
        await _store.ProvisionAsync(Tenant(), "dana@acme.example");

        var bound = await _store.TryBindFirstAdminAsync("tnt_acme", "random@acme.example", "entra-acme:oid-random", T0);

        bound.Should().BeFalse();
        (await _store.IsTenantAdminAsync("tnt_acme", "entra-acme:oid-random")).Should().BeFalse();
    }

    [Fact]
    public async Task IsTenantAdmin_IsTrueOnlyAfterBinding_AndOnlyWithinThatTenant()
    {
        await _store.ProvisionAsync(Tenant(), "dana@acme.example");
        await _store.ProvisionAsync(Tenant(tenantId: "tnt_other", entraTenantId: "entra-other"), "dana@acme.example");

        (await _store.IsTenantAdminAsync("tnt_acme", "entra-acme:oid-dana")).Should().BeFalse("nothing is bound yet");

        _ = await _store.TryBindFirstAdminAsync("tnt_acme", "dana@acme.example", "entra-acme:oid-dana", T0);

        (await _store.IsTenantAdminAsync("tnt_acme", "entra-acme:oid-dana")).Should().BeTrue();
        (await _store.IsTenantAdminAsync("tnt_other", "entra-acme:oid-dana"))
            .Should()
            .BeFalse("a tenant admin is an admin of exactly one tenant");
    }

    // ---- #347: one canonical form for an Entra directory id -------------------------------------

    private const string CanonicalGuid = "a1b2c3d4-1111-2222-3333-444455556666";

    /// <summary>
    /// Every shape <c>Guid.TryParse</c> accepts and an operator can plausibly paste. Each one used
    /// to provision a row that no token from that directory could ever match, because a token's
    /// <c>tid</c> is always the <c>"D"</c> form.
    /// </summary>
    public static TheoryData<string> PasteVariantsOfTheSameDirectory() =>
        [
            "A1B2C3D4-1111-2222-3333-444455556666",
            "  a1b2c3d4-1111-2222-3333-444455556666  ",
            "{a1b2c3d4-1111-2222-3333-444455556666}",
            "(A1B2C3D4-1111-2222-3333-444455556666)",
            "a1b2c3d4111122223333444455556666",
        ];

    [Theory]
    [MemberData(nameof(PasteVariantsOfTheSameDirectory))]
    public async Task ADirectoryIdPastedInAnyAcceptedShape_ResolvesFromTheTokensCanonicalForm(string pasted)
    {
        _ = await _store.ProvisionAsync(Tenant(entraTenantId: pasted), "dana@acme.example");

        // The read side is what a real sign-in does: the token's `tid` claim, always canonical.
        var found = await _store.FindByEntraTenantIdAsync(CanonicalGuid);

        _ = found.Should().NotBeNull();
        _ = found!.TenantId.Should().Be("tnt_acme");
        _ = found
            .EntraTenantId.Should()
            .Be(
                CanonicalGuid,
                "the stored value is canonicalised on write, so an operator reading the row sees the "
                    + "same string the token carries"
            );
    }

    [Theory]
    [MemberData(nameof(PasteVariantsOfTheSameDirectory))]
    public async Task ASecondTenantClaimingTheSameDirectoryInAnotherShape_IsRefused(string pasted)
    {
        _ = await _store.ProvisionAsync(Tenant(entraTenantId: CanonicalGuid), "dana@acme.example");

        var outcome = await _store.ProvisionAsync(
            Tenant(tenantId: "tnt_impostor", entraTenantId: pasted),
            "eve@impostor.example"
        );

        _ = outcome
            .Should()
            .Be(
                TenantProvisionOutcome.EntraTenantIdClaimed,
                "the duplicate check runs on the canonical form, or a second tenant could claim the "
                    + "same directory just by re-shaping the id"
            );
    }

    [Fact]
    public async Task ANonGuidDirectoryId_IsStillAcceptedAndStillMatchesCaseInsensitively()
    {
        // The normalizer is not a validator. Deployments seeded with a non-GUID placeholder (every
        // test above this line uses one) must keep resolving.
        _ = await _store.ProvisionAsync(Tenant(entraTenantId: "  Entra-ACME  "), "dana@acme.example");

        _ = (await _store.FindByEntraTenantIdAsync("entra-acme")).Should().NotBeNull();
    }

    [Fact]
    public async Task TheRepair_RewritesEveryLegacyShapeToTheCanonicalForm()
    {
        // Rows written by a build that predates the normalisation - or by a build rolled back to
        // one - keep whatever shape they were given. Only a repair pass can reach them.
        await SeedRawAsync("tnt_upper", "A1B2C3D4-1111-2222-3333-444455556666");
        await SeedRawAsync("tnt_braced", "{b1b2c3d4-1111-2222-3333-444455556666}");
        await SeedRawAsync("tnt_nohyphen", "c1b2c3d4111122223333444455556666");
        await SeedRawAsync("tnt_already", "d1b2c3d4-1111-2222-3333-444455556666");

        var result = await _store.NormalizeEntraTenantIdsAsync();

        _ = result.Rewritten.Should().Be(3, "the already-canonical row must not be counted or touched");
        _ = result.SkippedCollisions.Should().Be(0, "none of these fold onto each other");
        _ = (await ReadRawAsync("tnt_upper")).Should().Be(CanonicalGuid);
        _ = (await ReadRawAsync("tnt_braced")).Should().Be("b1b2c3d4-1111-2222-3333-444455556666");
        _ = (await ReadRawAsync("tnt_nohyphen")).Should().Be("c1b2c3d4-1111-2222-3333-444455556666");
        _ = (await ReadRawAsync("tnt_already")).Should().Be("d1b2c3d4-1111-2222-3333-444455556666");
    }

    [Fact]
    public async Task TheRepair_LeavesACollidingRowAloneRatherThanFailingTheStartup()
    {
        // Two rows folding onto one value would violate ux_tenants_entra. The repair runs on every
        // startup, so an exception here is a host that cannot boot and has no in-product way out.
        await SeedRawAsync("tnt_canonical", CanonicalGuid);
        await SeedRawAsync("tnt_shouty", "A1B2C3D4-1111-2222-3333-444455556666");

        var result = await _store.NormalizeEntraTenantIdsAsync();

        _ = result.Rewritten.Should().Be(0);
        _ = result
            .SkippedCollisions.Should()
            .Be(
                1,
                "the colliding row is not rewritten, but it must be COUNTED so the startup log can warn "
                    + "that a tenant was left unreachable rather than silently returning a smaller "
                    + "rewritten total"
            );
        _ = (await ReadRawAsync("tnt_canonical")).Should().Be(CanonicalGuid);
        _ = (await ReadRawAsync("tnt_shouty"))
            .Should()
            .Be(
                "A1B2C3D4-1111-2222-3333-444455556666",
                "the row is left exactly as it was - unreachable, but visible to an operator"
            );
    }

    [Fact]
    public async Task TheRepair_IsIdempotent()
    {
        await SeedRawAsync("tnt_upper", "A1B2C3D4-1111-2222-3333-444455556666");

        _ = (await _store.NormalizeEntraTenantIdsAsync()).Rewritten.Should().Be(1);
        _ = (await _store.NormalizeEntraTenantIdsAsync()).Rewritten.Should().Be(0);
        _ = (await ReadRawAsync("tnt_upper")).Should().Be(CanonicalGuid);
    }

    /// <summary>
    /// Inserts a tenant row with its <c>entra_tenant_id</c> written EXACTLY as given, bypassing the
    /// store's normalisation. That is the only way to reproduce a row an older build wrote.
    /// </summary>
    private async Task SeedRawAsync(string tenantId, string entraTenantId)
    {
        // Provisioning one row through the store first is what creates the schema; after that the
        // raw inserts have a table to land in.
        _ = await _store.FindByEntraTenantIdAsync("schema-warmup");

        await using var connection = await _factory.GetConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tenants (tenant_id, entra_tenant_id, display_name, status, created_at, created_by)
            VALUES ($tenantId, $entraTenantId, 'Seeded', 'active', 0, 'test');
            """;
        _ = command.Parameters.AddWithValue("$tenantId", tenantId);
        _ = command.Parameters.AddWithValue("$entraTenantId", entraTenantId);
        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> ReadRawAsync(string tenantId)
    {
        await using var connection = await _factory.GetConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT entra_tenant_id FROM tenants WHERE tenant_id = $tenantId;";
        _ = command.Parameters.AddWithValue("$tenantId", tenantId);
        return await command.ExecuteScalarAsync() as string;
    }
}
