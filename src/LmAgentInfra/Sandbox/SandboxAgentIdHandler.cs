using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// A <see cref="DelegatingHandler"/> that stamps <see cref="GatewayAuthHeaders.AgentIdHeader"/> on each
/// outgoing sandbox MCP request from a per-request resolver (gateway ADR 0040).
/// </summary>
/// <remarks>
/// <para>
/// One sandbox MCP client serves a conversation's primary agent AND every sub-agent that inherits its
/// tools, so the agent id cannot be fixed when the client is built. The resolver is evaluated on the
/// calling async flow at send time — typically <see cref="SandboxSubAgentIdentity.ForCurrentFlow"/>,
/// which reads the ambient sub-agent (<c>AgentActorScope</c>).
/// </para>
/// <para>
/// A null or empty resolved id OMITS the header (the primary/default agent); any value already on the
/// request is removed first so a static header can never leak the wrong identity. A non-empty invalid id
/// throws <see cref="ArgumentException"/> before the request is sent — never silently the default agent.
/// </para>
/// </remarks>
public sealed class SandboxAgentIdHandler : DelegatingHandler
{
    private readonly Func<string?> _resolveAgentId;

    /// <summary>Creates the handler around <paramref name="resolveAgentId"/>, evaluated once per request.</summary>
    public SandboxAgentIdHandler(Func<string?> resolveAgentId)
    {
        _resolveAgentId = resolveAgentId ?? throw new ArgumentNullException(nameof(resolveAgentId));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        _ = request.Headers.Remove(GatewayAuthHeaders.AgentIdHeader);
        var agentId = _resolveAgentId();
        if (!string.IsNullOrEmpty(agentId))
        {
            SandboxAgentId.ThrowIfInvalid(agentId, nameof(agentId));
            _ = request.Headers.TryAddWithoutValidation(GatewayAuthHeaders.AgentIdHeader, agentId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
