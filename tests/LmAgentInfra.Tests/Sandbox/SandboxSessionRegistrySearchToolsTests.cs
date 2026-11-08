using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// Issue #931: a host that opts in asks a new sandbox for the gateway's search tools, but only on a gateway new enough
/// to accept the request. An older gateway rejects the whole create on an unknown field, so it must never see one.
/// </summary>
public class SandboxSessionRegistrySearchToolsTests
{
    private const string Created = """{"session_id":"sess-1","container_id":"c-1"}""";

    [Theory]
    [InlineData("0.1.21")]
    [InlineData("0.1.22")]
    public async Task OptedIn_GatewayNewEnough_AsksForSkillSearchAndNotAgentSearch(string version)
    {
        var gateway = new Gateway($$"""{"version":"{{version}}"}""");
        await using var registry = CreateRegistry(gateway, offerSearchTools: true);

        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

        using var sent = JsonDocument.Parse(gateway.CreateBody!);
        var skills = sent.RootElement.GetProperty("skills");
        skills.GetProperty("search_tool").GetBoolean().Should().BeTrue();
        skills
            .GetProperty("agents_search_tool")
            .GetBoolean()
            .Should()
            .BeFalse("explicit: a gateway env default must not list a tool that finds nothing for a named agent");
        skills.TryGetProperty("default_tier", out _).Should().BeFalse("asking for search must not change the tiers");
    }

    [Theory]
    [InlineData("""{"version":"0.1.20-fsfix"}""")] // what the live gateway ran when this was written
    [InlineData("""{"version":"0.1.20"}""")]
    [InlineData("""{"version":"dev-build"}""")] // unreadable counts as older
    [InlineData("""{"name":"no version"}""")]
    [InlineData("not json")]
    public async Task OptedIn_GatewayOlderOrUnknown_CreateOmitsTheSkillsBlock(string info)
    {
        var gateway = new Gateway(info);
        await using var registry = CreateRegistry(gateway, offerSearchTools: true);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

        session.SessionId.Should().Be("sess-1", "the session must still be created");
        using var sent = JsonDocument.Parse(gateway.CreateBody!);
        sent.RootElement.TryGetProperty("skills", out _).Should().BeFalse("an older gateway rejects the unknown field");
    }

    /// <summary>The probe only decides whether to ask for the tools, so its failure must not become a failed create.</summary>
    [Fact]
    public async Task OptedIn_VersionProbeThrows_TheCreateStillRunsWithoutTheSkillsBlock()
    {
        var gateway = new Gateway(info: null);
        await using var registry = CreateRegistry(gateway, offerSearchTools: true);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

        session.SessionId.Should().Be("sess-1");
        gateway.ProbeCount.Should().Be(1, "guard: the probe ran and failed");
        using var sent = JsonDocument.Parse(gateway.CreateBody!);
        sent.RootElement.TryGetProperty("skills", out _).Should().BeFalse();
    }

    [Fact]
    public async Task NotOptedIn_NeverProbesTheGateway_AndSendsNoSkillsBlock()
    {
        var gateway = new Gateway("""{"version":"0.1.21"}""");
        await using var registry = CreateRegistry(gateway, offerSearchTools: false);

        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

        gateway.ProbeCount.Should().Be(0, "a host that does not wire the tools up pays nothing and changes nothing");
        using var sent = JsonDocument.Parse(gateway.CreateBody!);
        sent.RootElement.TryGetProperty("skills", out _).Should().BeFalse();
    }

    private static SandboxSessionRegistry CreateRegistry(Gateway gateway, bool offerSearchTools)
    {
        var options = new SandboxGatewayOptions
        {
            BaseUrl = "http://localhost:3000",
            OfferSearchTools = offerSearchTools,
        };
        var lifetime = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new Gateway("{}"))
        );
        return new SandboxSessionRegistry(
            lifetime,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(gateway),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmagentinfra-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );
    }

    /// <param name="info">The body of <c>GET /</c>; null makes that request fail at the transport.</param>
    private sealed class Gateway(string? info) : HttpMessageHandler
    {
        private int _probeCount;

        public string? CreateBody { get; private set; }

        public int ProbeCount => Volatile.Read(ref _probeCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                CreateBody = await request.Content!.ReadAsStringAsync(ct);
                return Json(HttpStatusCode.Created, Created);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/")
            {
                _ = Interlocked.Increment(ref _probeCount);
                return info is null
                    ? throw new HttpRequestException("the probe could not connect")
                    : Json(HttpStatusCode.OK, info);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
