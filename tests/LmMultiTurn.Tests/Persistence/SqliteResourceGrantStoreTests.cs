using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Pins <see cref="SqliteResourceGrantStore"/>, and specifically that its two readers of the
/// <c>role</c> column agree with each other (#387).
/// </summary>
/// <remarks>
/// The column carries a <c>CHECK (role IN ('viewer','editor'))</c>, so under normal operation no
/// unrecognised value can be written and the two readers cannot be told apart. That is exactly why
/// this is worth pinning: the disagreement is invisible until a row arrives from somewhere the
/// constraint did not cover - data restored from a build that predates it, or a manual edit during
/// an incident - and at that moment the same row means "no access" to a point read and "viewer" to
/// the listing the owner is looking at.
/// </remarks>
public sealed class SqliteResourceGrantStoreTests : IAsyncLifetime
{
    private const string Tenant = "tnt_acme";
    private const string Subject = "entra-tid:subject-oid";

    private static readonly ResourceRef Conversation = new(ResourceTypes.Conversation, "thread-1");

    private string _databasePath = null!;
    private SqliteConnectionFactory _factory = null!;
    private SqliteResourceGrantStore _store = null!;

    public Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"grants_{Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory(_databasePath);
        _store = new SqliteResourceGrantStore(_factory);
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

    [Fact]
    public async Task AGrantWrittenNormally_ReadsTheSameWayFromBothReaders()
    {
        // Non-vacuity, and it comes first: without it, a store that returned nothing at all from
        // either reader would satisfy every agreement assertion below.
        await _store.GrantAsync(
            new ResourceGrant
            {
                TenantId = Tenant,
                Resource = Conversation,
                SubjectId = Subject,
                Role = GrantRole.Editor,
                GrantedBy = "owner",
                GrantedAt = DateTimeOffset.UnixEpoch,
                ExpiresAt = null,
            },
            CancellationToken.None
        );

        var found = await _store.FindGrantAsync(
            Tenant,
            Conversation,
            Subject,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None
        );
        var listed = await _store.ListGrantsForResourceAsync(Tenant, Conversation, CancellationToken.None);

        _ = found.Should().Be(GrantRole.Editor);
        _ = listed.Should().ContainSingle().Which.Role.Should().Be(GrantRole.Editor);
    }

    [Fact]
    public async Task ARoleTheConstraintNeverAllowed_IsAbsentFromBothReaders()
    {
        // Seeded by direct SQL with the CHECK constraint dropped, because the point is precisely a
        // row the constraint would have refused. Going through GrantAsync could not produce one.
        await SeedRawRoleAsync("owner");

        var found = await _store.FindGrantAsync(
            Tenant,
            Conversation,
            Subject,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None
        );
        var listed = await _store.ListGrantsForResourceAsync(Tenant, Conversation, CancellationToken.None);

        _ = found.Should().BeNull("an unrecognised role confers nothing");

        // The listing used to answer Viewer here, disagreeing with the point read about the same
        // row. Presenting it as a viewer grant would also have been a lie to the owner reading the
        // share list: the point read denies it, so it grants nothing to anybody.
        _ = listed
            .Should()
            .BeEmpty(
                "both readers must agree, and the fail-closed answer is the one that agrees with the "
                    + "documented contract of ParseRole"
            );
    }

    [Fact]
    public async Task AnUnrecognisedRow_DoesNotHideTheValidGrantsBesideIt()
    {
        // Skipping a row must not become skipping the read. A break instead of a continue - or a
        // guard placed one level out - would truncate the share list at the bad row and silently
        // hide every grant ordered after it.
        await SeedRawRoleAsync("owner", subject: "entra-tid:aaa-first");
        await _store.GrantAsync(
            new ResourceGrant
            {
                TenantId = Tenant,
                Resource = Conversation,
                SubjectId = "entra-tid:zzz-last",
                Role = GrantRole.Viewer,
                GrantedBy = "owner",
                GrantedAt = DateTimeOffset.UnixEpoch,
                ExpiresAt = null,
            },
            CancellationToken.None
        );

        var listed = await _store.ListGrantsForResourceAsync(Tenant, Conversation, CancellationToken.None);

        _ = listed.Should().ContainSingle().Which.SubjectId.Should().Be("entra-tid:zzz-last");
    }

    /// <summary>
    /// Writes a <c>role</c> value the table's CHECK constraint refuses, by rebuilding the table
    /// without the constraint for the duration of the insert.
    /// </summary>
    private async Task SeedRawRoleAsync(string role, string subject = Subject)
    {
        // Force the schema into existence through the store's own initializer first, so the table
        // this replaces is the real one.
        _ = await _store.FindGrantAsync(
            Tenant,
            Conversation,
            subject,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None
        );

        await using var connection = await _factory.GetConnectionAsync(CancellationToken.None);

        using (var rebuild = connection.CreateCommand())
        {
            rebuild.CommandText = """
                CREATE TABLE IF NOT EXISTS resource_grants_raw (
                    tenant_id     TEXT NOT NULL,
                    resource_type TEXT NOT NULL,
                    resource_id   TEXT NOT NULL,
                    subject_id    TEXT NOT NULL,
                    role          TEXT NOT NULL,
                    granted_by    TEXT NOT NULL,
                    granted_at    INTEGER NOT NULL,
                    expires_at    INTEGER,
                    PRIMARY KEY (tenant_id, resource_type, resource_id, subject_id)
                );
                INSERT INTO resource_grants_raw
                SELECT tenant_id, resource_type, resource_id, subject_id, role,
                       granted_by, granted_at, expires_at
                  FROM resource_grants;
                DROP TABLE resource_grants;
                ALTER TABLE resource_grants_raw RENAME TO resource_grants;
                """;
            _ = await rebuild.ExecuteNonQueryAsync();
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO resource_grants
                (tenant_id, resource_type, resource_id, subject_id, role,
                 granted_by, granted_at, expires_at)
            VALUES ($tenantId, $resourceType, $resourceId, $subjectId, $role, 'owner', 0, NULL);
            """;
        _ = insert.Parameters.AddWithValue("$tenantId", Tenant);
        _ = insert.Parameters.AddWithValue("$resourceType", Conversation.Type);
        _ = insert.Parameters.AddWithValue("$resourceId", Conversation.Id);
        _ = insert.Parameters.AddWithValue("$subjectId", subject);
        _ = insert.Parameters.AddWithValue("$role", role);
        _ = await insert.ExecuteNonQueryAsync();
    }
}
