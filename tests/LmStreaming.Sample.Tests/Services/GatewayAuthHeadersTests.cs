namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Unit tests for <see cref="GatewayAuthHeaders"/> — the single owner of the gateway's per-app bearer
/// header names and the "attach only when a key is configured" rule (gateway ADR 0029). The session
/// header is always present; the two app headers appear together or not at all.
/// </summary>
public class GatewayAuthHeadersTests
{
    [Fact]
    public void ForMcp_always_carries_the_session_header()
    {
        var headers = GatewayAuthHeaders.ForMcp("sess-1", appId: null, appKey: null);

        headers.Should().ContainKey(GatewayAuthHeaders.SessionHeader).WhoseValue.Should().Be("sess-1");
    }

    [Fact]
    public void ForMcp_adds_both_app_headers_when_a_key_is_configured()
    {
        var headers = GatewayAuthHeaders.ForMcp("sess-1", "code-review-daemon", "c2VjcmV0");

        headers[GatewayAuthHeaders.SessionHeader].Should().Be("sess-1");
        headers[GatewayAuthHeaders.AppIdHeader].Should().Be("code-review-daemon");
        headers[GatewayAuthHeaders.AppKeyHeader].Should().Be("c2VjcmV0");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("code-review-daemon", null)]
    [InlineData("code-review-daemon", "")]
    [InlineData("code-review-daemon", "   ")]
    [InlineData(null, "c2VjcmV0")]
    public void ForMcp_omits_both_app_headers_when_id_or_key_is_missing(string? appId, string? appKey)
    {
        var headers = GatewayAuthHeaders.ForMcp("sess-1", appId, appKey);

        headers.Should().ContainKey(GatewayAuthHeaders.SessionHeader);
        headers.Should().NotContainKey(GatewayAuthHeaders.AppIdHeader);
        headers.Should().NotContainKey(GatewayAuthHeaders.AppKeyHeader);
    }

    [Fact]
    public void ForMcp_adds_the_agent_header_for_a_named_agent()
    {
        var headers = GatewayAuthHeaders.ForMcp("sess-1", "app", "c2VjcmV0", agentId: "agent-1.abc");

        headers[GatewayAuthHeaders.AgentIdHeader].Should().Be("agent-1.abc");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ForMcp_never_sends_an_empty_agent_header_for_the_primary(string? agentId)
    {
        // The gateway answers an empty X-Sbx-Agent-ID with JSON-RPC -32602; the primary sends none.
        var headers = GatewayAuthHeaders.ForMcp("sess-1", "app", "c2VjcmV0", agentId);

        headers.Should().NotContainKey(GatewayAuthHeaders.AgentIdHeader);
    }

    [Fact]
    public void ApplyAgentId_rejects_an_invalid_id_and_clears_on_null()
    {
        var headers = new Dictionary<string, string> { [GatewayAuthHeaders.AgentIdHeader] = "old" };

        var act = () => GatewayAuthHeaders.ApplyAgentId(headers, "bad id");
        act.Should().Throw<ArgumentException>();

        GatewayAuthHeaders.ApplyAgentId(headers, null);
        headers.Should().NotContainKey(GatewayAuthHeaders.AgentIdHeader);
    }

    [Fact]
    public void IsConfigured_requires_both_id_and_non_blank_key()
    {
        GatewayAuthHeaders.IsConfigured("app", "key").Should().BeTrue();
        GatewayAuthHeaders.IsConfigured("app", null).Should().BeFalse();
        GatewayAuthHeaders.IsConfigured("app", "  ").Should().BeFalse();
        GatewayAuthHeaders.IsConfigured(null, "key").Should().BeFalse();
    }
}
