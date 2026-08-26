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
    private readonly string _tokenStoreDir = Path.Combine(
        Path.GetTempPath(),
        "codereviewdaemon-tests",
        Guid.NewGuid().ToString("N")
    );

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "codereviewdaemon-tests",
        Guid.NewGuid().ToString("N") + ".db"
    );

    private readonly string _controlSocketPath = Path.Combine(
        Path.GetTempPath(),
        "codereviewdaemon-tests",
        Guid.NewGuid().ToString("N") + ".sock"
    );

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        AppContext.SetSwitch("LmDotnetTools.ReleaseIdentity.TestHostDevelopmentIdentity", true);
        builder.UseEnvironment("Development");
        builder.UseSetting("Auth:TokenStoreDir", _tokenStoreDir);
        // Isolate the orchestration store (it migrates SQLite at construction) to a throwaway file so
        // booting the host for a test never touches a developer's review.db beside the binary.
        builder.UseSetting("CodeReviewDaemon:DatabasePath", _databasePath);
        builder.UseSetting("CodeReviewDaemon:ControlSocketPath", _controlSocketPath);
        // The daemon requires S2S mode and a base URL to boot (in-process path removed). A fake URL
        // satisfies the guard — these tests never exercise the S2S client, only the route surface.
        builder.UseSetting("CodeReviewDaemon:UseS2SReviewAgent", "true");
        builder.UseSetting("CodeReviewDaemon:LmStreamingBaseUrl", "http://localhost:9999");
        builder.UseSetting("CodeReviewDaemon:RequireAgentCollaboration", "false");
        builder.UseSetting("SandboxGateway:WorkspaceBasePath", Path.GetTempPath());
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

                try
                {
                    File.Delete(_controlSocketPath);
                }
                catch
                {
                    // best-effort temp cleanup
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
