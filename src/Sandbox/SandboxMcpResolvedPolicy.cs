namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>Gateway response snapshot. Configuration values may be sensitive.</summary>
public sealed class SandboxMcpResolvedPolicy
{
    public SandboxMcpEgress Egress { get; }
    public IReadOnlyList<SandboxMcpResolvedServer> Servers { get; }

    internal SandboxMcpResolvedPolicy(SandboxMcpEgress egress, IReadOnlyList<SandboxMcpResolvedServer> servers)
    {
        Egress = egress;
        Servers = Array.AsReadOnly(servers.ToArray());
    }
}

/// <summary>Gateway response snapshot. Configuration values may be sensitive.</summary>
public sealed class SandboxMcpResolvedServer
{
    public string Name { get; }
    public string Plugin { get; }
    public string Marketplace { get; }
    public string Transport { get; }
    public string? Url { get; }
    public SandboxMcpAuthMode Auth { get; }
    public IReadOnlyList<string> Headers { get; }
    public string? ProviderId { get; }
    public string? RuleId { get; }
    public IReadOnlyList<string> UnresolvedVars { get; }

    internal SandboxMcpResolvedServer(
        string name,
        string plugin,
        string marketplace,
        string transport,
        string? url,
        SandboxMcpAuthMode auth,
        IReadOnlyList<string> headers,
        string? providerId,
        string? ruleId,
        IReadOnlyList<string> unresolvedVars
    )
    {
        Name = name;
        Plugin = plugin;
        Marketplace = marketplace;
        Transport = transport;
        Url = url;
        Auth = auth;
        Headers = Array.AsReadOnly(headers.ToArray());
        ProviderId = providerId;
        RuleId = ruleId;
        UnresolvedVars = Array.AsReadOnly(unresolvedVars.ToArray());
    }
}
