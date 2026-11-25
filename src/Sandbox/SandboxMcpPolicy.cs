using System.Collections.ObjectModel;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>Whether the gateway derives egress rules for declared MCP servers.</summary>
public enum SandboxMcpEgress
{
    Manual,
    Auto,
}

/// <summary>How an MCP helper obtains authentication. Injected values stay outside the sandbox.</summary>
public enum SandboxMcpAuthMode
{
    None,
    Injected,
}

/// <summary>Secret-free setup for HTTP MCP libraries. Omit the create property to keep legacy behavior.</summary>
public sealed class SandboxMcpPolicy
{
    /// <summary>Maximum number of direct MCP endpoints accepted by the gateway for one sandbox.</summary>
    public const int MaxDirectEndpoints = 64;

    public SandboxMcpEgress? Egress { get; }
    public IReadOnlyDictionary<string, SandboxMcpServerPolicy> Servers { get; }
    public IReadOnlyList<SandboxDirectMcpServer> Direct { get; }

    public SandboxMcpPolicy(
        SandboxMcpEgress? egress = null,
        IReadOnlyDictionary<string, SandboxMcpServerPolicy>? servers = null
    )
        : this(egress, servers, []) { }

    public SandboxMcpPolicy(
        SandboxMcpEgress? egress,
        IReadOnlyDictionary<string, SandboxMcpServerPolicy>? servers,
        IReadOnlyList<SandboxDirectMcpServer> direct
    )
    {
        ArgumentNullException.ThrowIfNull(direct);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(direct.Count, MaxDirectEndpoints, nameof(direct));
        if (egress is { } mode && !Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(egress));
        }

        var copy = new Dictionary<string, SandboxMcpServerPolicy>(StringComparer.Ordinal);
        if (servers is not null)
        {
            foreach (var (name, policy) in servers)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(servers));
                ArgumentNullException.ThrowIfNull(policy, nameof(servers));
                copy.Add(name, policy);
            }
        }

        if (egress == SandboxMcpEgress.Auto && direct.Count > 0)
        {
            throw new ArgumentException("Direct MCP servers require manual egress.", nameof(direct));
        }

        foreach (var declaration in direct)
        {
            ArgumentNullException.ThrowIfNull(declaration, nameof(direct));
            ArgumentException.ThrowIfNullOrWhiteSpace(declaration.Marketplace, nameof(direct));
            ArgumentException.ThrowIfNullOrWhiteSpace(declaration.Name, nameof(direct));
            ArgumentException.ThrowIfNullOrWhiteSpace(declaration.Url, nameof(direct));
        }

        Egress = egress;
        Servers = new ReadOnlyDictionary<string, SandboxMcpServerPolicy>(copy);
        Direct = Array.AsReadOnly(direct.ToArray());
    }
}

/// <summary>An approved, fixed HTTPS endpoint associated with an app grant. Supply egress policy separately.</summary>
public sealed record SandboxDirectMcpServer(string Marketplace, string Name, string Url);

/// <summary>One server override. Header names are public; credential values belong in the app's token store.</summary>
public sealed class SandboxMcpServerPolicy
{
    public SandboxMcpAuthMode? Auth { get; }
    public IReadOnlyList<string>? Headers { get; }
    public int? CacheTtlSeconds { get; }
    public bool? Enabled { get; }
    public string? Url { get; }

    public SandboxMcpServerPolicy(
        SandboxMcpAuthMode? auth = null,
        IReadOnlyList<string>? headers = null,
        int? cacheTtlSeconds = null,
        bool? enabled = null,
        string? url = null
    )
    {
        if (auth is { } mode && !Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(auth));
        }

        if (cacheTtlSeconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cacheTtlSeconds));
        }

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(header, nameof(headers));
            }
        }

        if (url is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);
        }

        Auth = auth;
        Headers = headers is null ? null : Array.AsReadOnly(headers.ToArray());
        CacheTtlSeconds = cacheTtlSeconds;
        Enabled = enabled;
        Url = url;
    }
}
