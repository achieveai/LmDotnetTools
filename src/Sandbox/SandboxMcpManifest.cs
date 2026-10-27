namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>The producer-reported egress state, independent of generated library freshness.</summary>
public enum SandboxMcpManifestEgress
{
    Auto,
    Manual,
    Stale,
}

/// <summary>Gateway response snapshot. Configuration values may be sensitive.</summary>
public sealed class SandboxMcpManifest
{
    public string Schema { get; }
    public string SessionId { get; }
    public IReadOnlyList<SandboxMcpManifestServer> Servers { get; }

    internal SandboxMcpManifest(string schema, string sessionId, IReadOnlyList<SandboxMcpManifestServer> servers)
    {
        Schema = schema;
        SessionId = sessionId;
        Servers = Array.AsReadOnly(servers.ToArray());
    }
}

/// <summary>Gateway response snapshot. Configuration values may be sensitive.</summary>
public sealed class SandboxMcpManifestServer
{
    public string Name { get; }
    public string Plugin { get; }
    public string Marketplace { get; }
    public string Transport { get; }
    public bool Supported { get; }
    public string? UnsupportedReason { get; }
    public string? Url { get; }
    public string? UrlTemplate { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public SandboxMcpManifestAuth Auth { get; }
    public SandboxMcpManifestEgress Egress { get; }
    public string? Note { get; }
    public IReadOnlyList<string> UnresolvedVars { get; }
    public SandboxMcpManifestGenerated? Generated { get; }

    internal SandboxMcpManifestServer(
        string name,
        string plugin,
        string marketplace,
        string transport,
        bool supported,
        string? unsupportedReason,
        string? url,
        string? urlTemplate,
        IReadOnlyDictionary<string, string> headers,
        SandboxMcpManifestAuth auth,
        SandboxMcpManifestEgress egress,
        string? note,
        IReadOnlyList<string> unresolvedVars,
        SandboxMcpManifestGenerated? generated
    )
    {
        Name = name;
        Plugin = plugin;
        Marketplace = marketplace;
        Transport = transport;
        Supported = supported;
        UnsupportedReason = unsupportedReason;
        Url = url;
        UrlTemplate = urlTemplate;
        Headers = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(headers, StringComparer.Ordinal)
        );
        Auth = auth;
        Egress = egress;
        Note = note;
        UnresolvedVars = Array.AsReadOnly(unresolvedVars.ToArray());
        Generated = generated;
    }
}

/// <summary>Gateway response snapshot. Configuration values may be sensitive.</summary>
public sealed class SandboxMcpManifestAuth
{
    public SandboxMcpAuthMode Mode { get; }
    public string? Provider { get; }
    public IReadOnlyList<string> Headers { get; }

    internal SandboxMcpManifestAuth(SandboxMcpAuthMode mode, string? provider, IReadOnlyList<string> headers)
    {
        Mode = mode;
        Provider = provider;
        Headers = Array.AsReadOnly(headers.ToArray());
    }
}

/// <summary>Gateway response snapshot. Configuration values may be sensitive.</summary>
public sealed class SandboxMcpManifestGenerated
{
    public string Plugin { get; }
    public string Marketplace { get; }
    public string? Skill { get; }
    public string? ToolsHash { get; }
    public string LibPath { get; }

    internal SandboxMcpManifestGenerated(
        string plugin,
        string marketplace,
        string? skill,
        string? toolsHash,
        string libPath
    )
    {
        Plugin = plugin;
        Marketplace = marketplace;
        Skill = skill;
        ToolsHash = toolsHash;
        LibPath = libPath;
    }
}
