using System.Net;
using System.Net.Sockets;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>Shared test fixtures: a valid client secret and a helper to wire a <see cref="SandboxClient"/> over a <see cref="FakeGatewayHandler"/>.</summary>
internal static class TestSupport
{
    public static readonly string ValidSecret = Convert.ToBase64String(new byte[32]);

    /// <summary>
    /// Returns a fresh loopback address bound to an OS-assigned free TCP port. Every caller gets its
    /// own independent <see cref="Uri"/> instance rather than sharing one hardcoded port (previously
    /// a single <c>static readonly</c> <c>http://127.0.0.1:3000</c>) — a hardcoded port can collide
    /// with an unrelated local service the dev/CI machine happens to have bound to it (3000 is a very
    /// common default, e.g. Node dev servers), which is nondeterministic across environments and
    /// would only matter here because <see cref="SandboxClientTransportTests.OwnedClient_Dispose_DisposesTransport"/>
    /// builds a genuinely OWNED <see cref="HttpClientHandler"/> (not routed through
    /// <see cref="FakeGatewayHandler"/>) against this address.
    /// </summary>
    public static Uri NewLoopbackAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }

    public static (SandboxClient Client, FakeGatewayHandler Handler) CreateBorrowedClient(
        TimeSpan? transportTimeout = null,
        string appId = "app-1",
        string? clientSecret = null
    )
    {
        var handler = new FakeGatewayHandler();
        var serverAddress = NewLoopbackAddress();
        var httpClient = new HttpClient(handler) { BaseAddress = serverAddress };
        var options = new SandboxClientOptions(
            serverAddress,
            appId,
            clientSecret ?? ValidSecret,
            TimeSpan.FromMinutes(5),
            transportTimeout ?? TimeSpan.FromSeconds(30)
        );
        var client = new SandboxClient(options, httpClient);
        return (client, handler);
    }
}
