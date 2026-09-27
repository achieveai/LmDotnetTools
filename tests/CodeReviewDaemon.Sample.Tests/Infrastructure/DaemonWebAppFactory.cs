using System.Net;
using System.Net.Sockets;
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

    // Program.cs's Main runs an EnsureHostContractAsync preflight (a real GET
    // api/conversations/capabilities) against CodeReviewDaemon:LmStreamingBaseUrl before the host
    // finishes starting. A fake, unreachable URL (e.g. localhost:9999) hangs that preflight until the
    // OS TCP connect timeout, so this factory stands up a tiny real loopback listener instead and
    // points the daemon at it. Answering for real (rather than swapping the LmStreamingS2SClient
    // registration in DI) keeps the production HttpClient — and its ReviewStageDeadlineMinutes-derived
    // Timeout wiring — exactly what Program.cs built, which is what DaemonS2SClientTimeoutWiringTests
    // pins.
    private readonly HttpListener _fakeReviewHost = new();
    private readonly int _fakeReviewHostPort = GetFreeTcpPort();
    private readonly CancellationTokenSource _fakeReviewHostCts = new();

    private const string CapabilitiesBody =
        "{\"schemaVersion\":1,\"messageIdempotency\":true,\"spawnSuppression\":true,\"rootReasoningEffort\":true,\"actionToolSuppression\":true}";

    public DaemonWebAppFactory()
    {
        _fakeReviewHost.Prefixes.Add($"http://127.0.0.1:{_fakeReviewHostPort}/");
        _fakeReviewHost.Start();
        _ = AcceptFakeReviewHostRequestsAsync(_fakeReviewHostCts.Token);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Auth:TokenStoreDir", _tokenStoreDir);
        // Isolate the orchestration store (it migrates SQLite at construction) to a throwaway file so
        // booting the host for a test never touches a developer's review.db beside the binary.
        builder.UseSetting("CodeReviewDaemon:DatabasePath", _databasePath);
        // The daemon requires S2S mode and a base URL to boot (in-process path removed). These tests
        // never exercise a review's own S2S traffic — only the route surface — but Main's startup
        // preflight still needs a host that actually answers, hence the loopback listener above.
        builder.UseSetting("CodeReviewDaemon:UseS2SReviewAgent", "true");
        builder.UseSetting("CodeReviewDaemon:LmStreamingBaseUrl", $"http://127.0.0.1:{_fakeReviewHostPort}");
        builder.UseSetting("SandboxGateway:WorkspaceBasePath", Path.GetTempPath());
    }

    private async Task AcceptFakeReviewHostRequestsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _fakeReviewHost.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _fakeReviewHost.GetContextAsync().WaitAsync(ct);
            }
            catch
            {
                // Listener stopped/disposed during shutdown, or the wait was cancelled — either way
                // there is nothing left to serve.
                return;
            }

            try
            {
                var body = System.Text.Encoding.UTF8.GetBytes(CapabilitiesBody);
                context.Response.ContentType = "application/json";
                context.Response.StatusCode = 200;
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, ct);
            }
            catch
            {
                // Best-effort fake response; a race during shutdown must not crash the accept loop.
            }
            finally
            {
                context.Response.OutputStream.Close();
            }
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
                // WithWebHostBuilder(...) clones this factory via MemberwiseClone, so the clone and the
                // original SHARE this listener/CTS by reference. Both get disposed (once per `using`
                // block), so every step here must tolerate running twice.
                try
                {
                    _fakeReviewHostCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // already torn down by the sibling factory instance
                }

                try
                {
                    _fakeReviewHost.Stop();
                    _fakeReviewHost.Close();
                }
                catch
                {
                    // best-effort listener teardown
                }

                try
                {
                    _fakeReviewHostCts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // already torn down by the sibling factory instance
                }

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
