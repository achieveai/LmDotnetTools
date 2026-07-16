using System.Net;
using System.Text;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the per-credential client ref-counting (task 37): the registry caches one
/// <c>SandboxClient</c>+transport per distinct (AppId, AppKey), ref-counted by live sessions, and
/// evicts+disposes it when the last session under that credential is destroyed — so credential rotation
/// / high-cardinality credentials cannot grow the client cache monotonically. A later create under an
/// evicted credential re-adds a fresh entry.
/// </summary>
public sealed class SandboxSessionRegistryClientEvictionTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";

    [Fact]
    public async Task PerCredentialClient_IsEvictedWhenItsLastSessionIsDestroyed_AndReAddedOnRecreate()
    {
        var (registry, _) = CreateRegistry();
        await using var _reg = registry;

        var credA = new SandboxCredential("app-a", string.Empty);
        var credB = new SandboxCredential("app-b", string.Empty);

        // Two sessions under two distinct credentials → two per-credential client entries.
        _ = await registry.GetOrCreateSessionAsync("wsA", CancellationToken.None, credA);
        _ = await registry.GetOrCreateSessionAsync("wsB", CancellationToken.None, credB);
        registry.PerCredentialClientCount.Should().Be(2);

        // Destroying every session for workspace wsA releases credA's last session → its client is evicted.
        await registry.DestroyWorkspaceSessionAsync("wsA");
        registry.PerCredentialClientCount.Should().Be(1);

        // A later create under the evicted credential re-adds a fresh entry.
        _ = await registry.GetOrCreateSessionAsync("wsA", CancellationToken.None, credA);
        registry.PerCredentialClientCount.Should().Be(2);
    }

    private static (SandboxSessionRegistry Registry, StubHandler Handler) CreateRegistry()
    {
        var createCounter = 0;

        HttpResponseMessage Respond(HttpRequestMessage req)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post && path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref createCounter);
                var body =
                    "{\"session_id\":\"sess-" + n + "\",\"container_id\":\"c-" + n + "\",\"volumes\":{\"workspace\":"
                    + "{\"container_path\":\"/workspace\",\"read_only\":false,\"id\":7}}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }

            // DELETE (destroy) and any GET (liveness) succeed.
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        var handler = new StubHandler(Respond);
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl };
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        );

        var auth = new AuthOptions();
        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(handler),
            auth,
            new AuthSharedSecret(auth)
        );

        return (registry, handler);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
