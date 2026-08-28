using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Pins the two obligations
/// <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite.SqliteConnectionFactory"/>'s pooled connection owes its
/// factory on disposal, both of which have been broken in this file's history and neither of which
/// any store-level test can observe:
/// <list type="number">
/// <item>
/// The native SQLite handle is actually closed, so the database FILE is released. Every store test
/// runs against an in-memory database and would pass with the handle leaked; the leak only surfaces
/// where a real file has to be renamed or deleted -- which is exactly what
/// <c>publish-launch.ps1</c>'s destination swap does, and where it was found.
/// </item>
/// <item>
/// The semaphore permit is returned. A leaked permit is strictly worse than a leaked handle: the GC
/// eventually reclaims a handle, but nothing ever reposts a lost permit, so a factory that loses
/// <c>maxConnections</c> of them blocks forever on the next <c>GetConnectionAsync</c> with no error
/// anywhere.
/// </item>
/// </list>
/// </summary>
public class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _root = Directory
        .CreateDirectory(Path.Combine(Path.GetTempPath(), "sqlfac-" + Guid.NewGuid().ToString("N")))
        .FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task DisposeAsync_ReleasesTheFileHandle_SoTheDatabaseDirectoryCanBeRenamed()
    {
        // The litmus for the real defect. PooledConnection.DisposeAsync used to set _disposed BEFORE
        // delegating to base.DisposeAsync(), and DbConnection's default async disposal calls the
        // synchronous Dispose(), which virtually dispatches back into this same instance's
        // Dispose(bool) -- where the already-true guard short-circuited and skipped
        // base.Dispose(disposing) entirely. The native handle was therefore never closed, the file
        // stayed locked, and Directory.Move failed with "access is denied".
        //
        // Renaming the containing DIRECTORY (not deleting the file) is deliberate: it is precisely
        // the operation publish-launch.ps1's atomic swap performs over a deployed notify-waits.db,
        // and it is the operation that actually failed in CI.
        //
        // The factory sets Pooling = true, so ClearAllPools() below is REQUIRED even when disposal
        // is correct -- a pooled connection's native handle deliberately outlives Dispose. That does
        // not make this test vacuous, because the bug bypassed the pool entirely: base.Dispose was
        // never invoked, so the connection was never closed OR returned, and no amount of
        // pool-clearing reaches a handle the pool does not know it owns. ClearAllPools is therefore
        // the precise dividing line: it releases a correctly-disposed connection and cannot release
        // a leaked one.
        var dbDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(dbDirectory);
        var databasePath = Path.Combine(dbDirectory, "notify-waits.db");

        await using (var factory = new SqliteConnectionFactory(databasePath))
        {
            await using var connection = await factory.GetConnectionAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE probe (id INTEGER PRIMARY KEY);";
            _ = await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();

        var renamed = Path.Combine(_root, "data-renamed");
        var rename = () => Directory.Move(dbDirectory, renamed);

        rename
            .Should()
            .NotThrow(
                "disposing the connection must actually close it and return it to the pool -- only then can ClearAllPools release the file and let its directory be renamed"
            );
        File.Exists(Path.Combine(renamed, "notify-waits.db")).Should().BeTrue();
    }

    [Fact]
    public async Task DisposedConnections_ReturnTheirPermits_SoTheFactoryDoesNotStarve()
    {
        // Bounded to maxConnections. If disposal ever fails to release the permit, the (N+1)th
        // acquisition blocks forever -- so this test asserts against a timeout rather than simply
        // awaiting, which would hang the whole suite instead of failing it.
        const int maxConnections = 2;
        var databasePath = Path.Combine(_root, "permits.db");
        await using var factory = new SqliteConnectionFactory(databasePath, maxConnections);

        // Three full cycles of saturate-then-release. One cycle would pass even if permits were
        // released only once; the point is that the pool is still whole after repeated use.
        //
        // The acquire loop and the release loop are split by a `try`/`finally` (#372): without it, a
        // throw partway through acquiring `maxConnections` connections would leave whatever was
        // already acquired holding its permit forever. The `try` body today contains only the acquire
        // loop itself -- no assertion currently lives inside it -- but the guard is written around the
        // loop rather than around each individual `GetConnectionAsync()` call so that anything added
        // inside this block later (an assertion, another acquire) stays covered by the same release
        // without the guard needing to be re-drawn. SqliteConnectionFactory.DisposeAsync then drains
        // the pool by re-acquiring every permit with no timeout, so even ONE leaked permit here wedges
        // this test's own `await using factory` teardown, which escalates from a failing test to an
        // aborted assembly (#362's failure mode, reached through a different door).
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var connections = new List<SqliteConnection>();
            try
            {
                for (var i = 0; i < maxConnections; i++)
                {
                    connections.Add(await factory.GetConnectionAsync());
                }
            }
            finally
            {
                foreach (var connection in connections)
                {
                    await connection.DisposeAsync();
                }
            }
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acquire = async () =>
        {
            await using var connection = await factory.GetConnectionAsync(cts.Token);
            connection.State.Should().Be(System.Data.ConnectionState.Open);
        };

        await acquire
            .Should()
            .NotThrowAsync(
                "every disposed connection must have returned its permit -- a lost permit is never reposted, so the factory would block here forever"
            );
    }

    [Fact]
    public async Task SynchronousDispose_AlsoReleasesItsPermit()
    {
        // DisposeAsync is the path production uses, but Dispose() is reachable through `using` and
        // through DbConnection's own async-to-sync fallback. Both must return the permit exactly
        // once: releasing twice would let the factory hand out more connections than its cap, which
        // is the opposite failure and equally silent.
        const int maxConnections = 1;
        var databasePath = Path.Combine(_root, "sync-permits.db");
        await using var factory = new SqliteConnectionFactory(databasePath, maxConnections);

        // Unlike DisposedConnections_ReturnTheirPermits_SoTheFactoryDoesNotStarve's acquire-loop /
        // release-loop split, acquire and dispose here are adjacent with nothing between them that
        // could throw and skip the release -- and PooledConnection.Dispose(bool) itself already
        // releases the permit from a `finally`, so it does so even if the underlying native close
        // faults. No acquire/finally/release span is needed for this loop (#372).
        for (var i = 0; i < 3; i++)
        {
            var connection = await factory.GetConnectionAsync();
            connection.Dispose();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var final = await factory.GetConnectionAsync(cts.Token);
        final.State.Should().Be(System.Data.ConnectionState.Open);

        // With maxConnections = 1 the permit count is directly observable: a double-release would
        // have let this second, concurrent acquisition succeed while `final` is still alive.
        using var overCap = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var acquireSecond = async () => await factory.GetConnectionAsync(overCap.Token);

        await acquireSecond
            .Should()
            .ThrowAsync<OperationCanceledException>(
                "the cap must still be enforced -- a permit released twice would silently raise the real concurrency limit above maxConnections"
            );
    }
}
