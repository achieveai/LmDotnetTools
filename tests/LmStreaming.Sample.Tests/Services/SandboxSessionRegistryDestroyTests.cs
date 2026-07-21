using System.Net;

namespace LmStreaming.Sample.Tests.Services;

public class SandboxSessionRegistryDestroyTests
{
    [Fact]
    public async Task DestroyWorkspaceSessionAsync_UnknownWorkspace_IsNoOp()
    {
        var deletes = 0;
        var handler = new CountingHandler(req =>
        {
            if (req.Method == HttpMethod.Delete) deletes++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var registry = new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(handler),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance));

        await registry.DestroyWorkspaceSessionAsync("never-created");

        deletes.Should().Be(0);
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
}
