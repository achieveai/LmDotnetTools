using System.Net;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// The sub-agent gateway id (gateway ADR 0040) and the per-request header stamp: stable from persistent
/// inputs, conversation-qualified, always valid, and never sent for the primary agent.
/// </summary>
public class SandboxSubAgentIdentityTests
{
    private const string RootThread = "thread-1791580790274-48fec4f20f2f493aacdf850c65f4a2fc";

    [Fact]
    public void ForSubAgent_IsTheSubAgentIdPlusAConversationTag()
    {
        // Pinned value: changing the derivation re-keys every live sub-agent's gateway state.
        SandboxSubAgentIdentity.ForSubAgent(RootThread, "agent-1").Should().Be("agent-1.61ab6f973dcaf4b8");
    }

    [Fact]
    public void ForSubAgent_DiffersAcrossConversations()
    {
        SandboxSubAgentIdentity
            .ForSubAgent(RootThread, "agent-1")
            .Should()
            .NotBe(SandboxSubAgentIdentity.ForSubAgent("thread-other", "agent-1"));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("café")]
    [InlineData("a-sub-agent-id-that-is-far-too-long-to-fit-beside-the-tag")]
    public void ForSubAgent_HashesAnIdTheGatewayCannotCarry_IntoAValidId(string subAgentId)
    {
        var id = SandboxSubAgentIdentity.ForSubAgent(RootThread, subAgentId);

        SandboxAgentId.IsValid(id).Should().BeTrue();
        id.Should().StartWith("sa-");
    }

    [Fact]
    public void TryGetSubAgentId_RoundTripsForItsOwnConversationOnly()
    {
        var id = SandboxSubAgentIdentity.ForSubAgent(RootThread, "agent-7");

        SandboxSubAgentIdentity.TryGetSubAgentId(id, RootThread, out var subAgentId).Should().BeTrue();
        subAgentId.Should().Be("agent-7");
        SandboxSubAgentIdentity.TryGetSubAgentId(id, "thread-other", out _).Should().BeFalse();
        SandboxSubAgentIdentity.TryGetSubAgentId("", RootThread, out _).Should().BeFalse();
        SandboxSubAgentIdentity.TryGetSubAgentId("ctx-probe", RootThread, out _).Should().BeFalse();
    }

    [Fact]
    public void ForCurrentFlow_IsNullForThePrimary_AndTheSubAgentIdUnderItsScope()
    {
        SandboxSubAgentIdentity.ForCurrentFlow(RootThread).Should().BeNull();
        using (AgentActorScope.Begin("agent-2"))
        {
            SandboxSubAgentIdentity
                .ForCurrentFlow(RootThread)
                .Should()
                .Be(SandboxSubAgentIdentity.ForSubAgent(RootThread, "agent-2"));
        }
    }

    [Fact]
    public async Task Handler_PrimaryFlow_OmitsTheHeader_EvenWhenTheRequestCarriedOne()
    {
        var (invoker, inner) = CreateInvoker(() => SandboxSubAgentIdentity.ForCurrentFlow(RootThread));
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://gateway/mcp");
        request.Headers.Add(GatewayAuthHeaders.AgentIdHeader, "stale-agent");

        using var _ = await invoker.SendAsync(request, CancellationToken.None);

        inner.AgentIds.Should().Equal([null]);
    }

    [Fact]
    public async Task Handler_SubAgentFlow_StampsItsGatewayId()
    {
        var (invoker, inner) = CreateInvoker(() => SandboxSubAgentIdentity.ForCurrentFlow(RootThread));

        using (AgentActorScope.Begin("agent-1"))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://gateway/mcp");
            using var _ = await invoker.SendAsync(request, CancellationToken.None);
        }

        inner.AgentIds.Should().Equal(["agent-1.61ab6f973dcaf4b8"]);
    }

    [Fact]
    public async Task Handler_InvalidResolvedId_ThrowsWithoutSending()
    {
        var (invoker, inner) = CreateInvoker(() => "bad id");
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://gateway/mcp");

        var act = async () => await invoker.SendAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        inner.AgentIds.Should().BeEmpty();
    }

    private static (HttpMessageInvoker Invoker, RecordingHandler Inner) CreateInvoker(Func<string?> resolve)
    {
        var inner = new RecordingHandler();
        return (new HttpMessageInvoker(new SandboxAgentIdHandler(resolve) { InnerHandler = inner }), inner);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string?> AgentIds { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            AgentIds.Add(
                request.Headers.TryGetValues(GatewayAuthHeaders.AgentIdHeader, out var values)
                    ? string.Join(",", values)
                    : null
            );
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
