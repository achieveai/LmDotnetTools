using System.Net;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Real MVC-pipeline coverage for issue #153 M2's inbound S2S guard, complementing the direct-filter
/// tests in <see cref="ConversationsControllerS2SAuthTests"/>. These stand up a minimal
/// <c>TestServer</c> hosting only <see cref="ConversationsController"/> (with fakes for its
/// dependencies) so requests flow through real routing + filter discovery. This is the regression
/// that the direct-filter tests cannot give: it fails if <c>[InboundS2SAuth]</c> is removed from the
/// controller or scoped to the wrong routes (BLOCKER #1), and proves the same-origin SPA path stays
/// reachable with the secret configured.
/// </summary>
public sealed class ConversationsControllerS2SPipelineTests
{
    private const string Secret = "s3cr3t-inbound-pipeline-value";
    private const string ListRoute = "/api/conversations";

    private static async Task<IHost> StartHostAsync(string? configuredSecret)
    {
        var store = new InMemoryConversationStore();
        var modeStore = new Mock<IChatModeStore>();
        modeStore
            .Setup(m => m.GetModeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string modeId, CancellationToken _) => SystemChatModes.GetById(modeId));

        var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance);

        var configData = new Dictionary<string, string?>();
        if (configuredSecret != null)
        {
            configData[InboundS2SAuthAttribute.SecretConfigKey] = configuredSecret;
        }

        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                _ = webBuilder
                    .UseTestServer()
                    .ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(configData))
                    .ConfigureServices(services =>
                    {
                        _ = services.AddSingleton<IConversationStore>(store);
                        _ = services.AddSingleton(pool);
                        _ = services.AddSingleton<IChatModeStore>(modeStore.Object);
                        _ = services.AddSingleton(Mock.Of<IWorkspaceStore>());
                        _ = services.AddSingleton(
                            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal());
                        _ = services.AddSingleton(new ConversationStatusResolver(store, store));
                        _ = services
                            .AddControllers()
                            .AddApplicationPart(typeof(ConversationsController).Assembly);
                    })
                    .Configure(app =>
                    {
                        _ = app.UseRouting();
                        _ = app.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
            })
            .StartAsync();

        return host;
    }

    [Fact]
    public async Task BrowserRequest_NoMarkers_IsReachable_WhenSecretConfigured()
    {
        // BLOCKER #1 regression: the SPA hits this route with plain fetch (no S2S markers); it must
        // stay reachable once the secret is configured.
        using var host = await StartHostAsync(Secret);
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(ListRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task S2SRequest_WithAppIdMarker_ButNoAuthHeader_Is401_WhenSecretConfigured()
    {
        // Proves the attribute is actually on the controller and armed by the caller-credential
        // marker: if [InboundS2SAuth] were removed/misscoped this would 200.
        using var host = await StartHostAsync(Secret);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
        request.Headers.TryAddWithoutValidation(SandboxCredential.AppIdHeader, "app-a");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task S2SRequest_WithMatchingAuthHeader_IsReachable_WhenSecretConfigured()
    {
        using var host = await StartHostAsync(Secret);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
        request.Headers.TryAddWithoutValidation(InboundS2SAuthAttribute.HeaderName, Secret);
        request.Headers.TryAddWithoutValidation(SandboxCredential.AppIdHeader, "app-a");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task S2SRequest_WithWrongAuthHeader_Is401_WhenSecretConfigured()
    {
        using var host = await StartHostAsync(Secret);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
        request.Headers.TryAddWithoutValidation(InboundS2SAuthAttribute.HeaderName, "totally-wrong");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AllRequests_Reachable_WhenSecretNotConfigured()
    {
        // Keyless dev path: no secret → guard disabled → even an S2S-marked request passes.
        using var host = await StartHostAsync(configuredSecret: null);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
        request.Headers.TryAddWithoutValidation(SandboxCredential.AppIdHeader, "app-a");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
