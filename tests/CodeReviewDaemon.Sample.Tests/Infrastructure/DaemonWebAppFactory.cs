using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Boots the <c>CodeReviewDaemon.Sample</c> host in-process (no bound ports — in-memory test server)
/// with an isolated, throwaway OAuth token-store directory so the test never touches a developer's
/// real <c>oauth-tokens</c> directory and leaves nothing behind.
/// </summary>
public sealed class DaemonWebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _tokenStoreDir =
        Path.Combine(Path.GetTempPath(), "codereviewdaemon-tests", Guid.NewGuid().ToString("N"));

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "codereviewdaemon-tests", Guid.NewGuid().ToString("N") + ".db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Auth:TokenStoreDir", _tokenStoreDir);
        // Isolate the orchestration store (it migrates SQLite at construction) to a throwaway file so
        // booting the host for a test never touches a developer's review.db beside the binary.
        builder.UseSetting("CodeReviewDaemon:DatabasePath", _databasePath);
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing)
            {
                if (Directory.Exists(_tokenStoreDir))
                {
                    try
                    {
                        Directory.Delete(_tokenStoreDir, recursive: true);
                    }
                    catch
                    {
                        // best-effort temp cleanup
                    }
                }

                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    try
                    {
                        if (File.Exists(_databasePath + suffix))
                        {
                            File.Delete(_databasePath + suffix);
                        }
                    }
                    catch
                    {
                        // best-effort temp cleanup
                    }
                }
            }
        }
    }
}
