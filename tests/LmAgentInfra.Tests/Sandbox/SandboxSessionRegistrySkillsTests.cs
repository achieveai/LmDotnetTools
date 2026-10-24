using System.Net;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// The skill-tier PATCH pass-through fails closed on the default agent. The SDK maps a null or empty
/// agent id to the DEFAULT agent, which every conversation on a shared sandbox session uses; one
/// <c>{reset, every skill available}</c> sent there would empty every conversation's <c>Skill</c> list.
/// </summary>
public class SandboxSessionRegistrySkillsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task PatchSkillTiers_NullOrEmptyAgent_ThrowsBeforeAnyHttpRequest(string? agentId)
    {
        var gateway = new RecordingGateway();
        await using var registry = CreateRegistry(gateway);

        var act = () =>
            registry.PatchSkillTiersAsync(
                "sess-1",
                agentId!,
                new Dictionary<string, SandboxSkillTier>
                {
                    ["/marketplaces/m/p/skills/s"] = SandboxSkillTier.Available,
                },
                reset: true
            );

        _ = await act.Should().ThrowAsync<ArgumentException>();
        gateway.RequestCount.Should().Be(0);
    }

    private static SandboxSessionRegistry CreateRegistry(RecordingGateway gateway)
    {
        var options = new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" };
        var lifetime = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new RecordingGateway())
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

    /// <summary>Counts every request and answers each with 500, so a request that slips through cannot pass.</summary>
    private sealed class RecordingGateway : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _ = Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
