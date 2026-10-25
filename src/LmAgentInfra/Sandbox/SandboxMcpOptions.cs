using System.Collections.ObjectModel;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>Operator configuration for sandbox MCP libraries. Never put tokens in this policy or sandbox env.</summary>
public sealed class SandboxMcpOptions
{
    public string Egress { get; set; } = "manual";
    public Dictionary<string, SandboxMcpServerOptions> Servers { get; set; } = new(StringComparer.Ordinal);
    public List<SandboxMcpCredentialBindingOptions> CredentialBindings { get; set; } = [];
}

/// <summary>Secret-free overrides for a marketplace server declaration.</summary>
public sealed class SandboxMcpServerOptions
{
    public string? Auth { get; set; }
    public string[]? Headers { get; set; }
    public int? CacheTtlSeconds { get; set; }
    public bool? Enabled { get; set; }
    public string? Url { get; set; }
}

/// <summary>Explicit permission for one creating app to use an existing provider at one HTTPS endpoint.</summary>
public sealed class SandboxMcpCredentialBindingOptions
{
    public string AppId { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string[] Headers { get; set; } = ["Authorization"];
    public int CacheTtlSeconds { get; set; } = 60;
    public string? Plugin { get; set; }
    public string? Marketplace { get; set; }
}

// Frozen metadata only: credentials remain in the existing providers/stores and are acquired on each cache miss.
internal sealed record SandboxMcpCredentialBinding(
    string Server,
    Uri Endpoint,
    string ProviderId,
    IReadOnlyList<string> Headers,
    int CacheTtlSeconds,
    string? Plugin,
    string? Marketplace
);

internal sealed record SandboxMcpSessionContext(
    string AppId,
    SandboxMcpPolicy Policy,
    IReadOnlyDictionary<string, SandboxMcpCredentialBinding> Bindings,
    IReadOnlyList<Uri> ProtectedEndpoints
);

internal static class SandboxMcpConfiguration
{
    internal static SandboxMcpSessionContext? Snapshot(SandboxMcpOptions? options, string appId)
    {
        if (options is null)
        {
            return null;
        }

        var egress = options.Egress switch
        {
            "auto" => SandboxMcpEgress.Auto,
            "manual" => SandboxMcpEgress.Manual,
            _ => throw new ArgumentException("MCP egress must be auto or manual."),
        };
        var servers = new Dictionary<string, SandboxMcpServerPolicy>(StringComparer.Ordinal);
        foreach (var (name, value) in options.Servers)
        {
            var auth = value.Auth switch
            {
                null => (SandboxMcpAuthMode?)null,
                "none" => SandboxMcpAuthMode.None,
                "injected" => SandboxMcpAuthMode.Injected,
                _ => throw new ArgumentException("MCP auth must be none or injected."),
            };
            var server = new SandboxMcpServerPolicy(
                auth,
                value.Headers,
                value.CacheTtlSeconds,
                value.Enabled,
                value.Url
            );
            if (server.Enabled != false && server.Auth != SandboxMcpAuthMode.None && server.Headers is { Count: 0 })
            {
                throw new ArgumentException(
                    "Injected MCP server Headers must contain at least one header name when specified."
                );
            }
            servers.Add(name, server);
        }

        var bindings = new Dictionary<string, SandboxMcpCredentialBinding>(StringComparer.Ordinal);
        var identities = new HashSet<(string AppId, string Server)>();
        var endpoints = new List<Uri>();
        foreach (var value in options.CredentialBindings)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value.AppId);
            ArgumentException.ThrowIfNullOrWhiteSpace(value.Server);
            ArgumentException.ThrowIfNullOrWhiteSpace(value.ProviderId);
            if (!identities.Add((value.AppId, value.Server)))
            {
                throw new ArgumentException("MCP credential bindings must be unique per app and server.");
            }

            if (!TryCredentialEndpoint(value.Url, out var endpoint))
            {
                throw new ArgumentException(
                    "MCP credential endpoints require HTTPS with canonical paths and without userinfo, query or fragment."
                );
            }

            if (
                value.CacheTtlSeconds is < 1 or > 300
                || value.Headers.Length == 0
                || value.Headers.Any(h => EgressHostMatcher.ValidateHeaderName(h) is not null)
            )
            {
                throw new ArgumentException(
                    "MCP bindings require credential header names and a cache TTL from 1 to 300 seconds."
                );
            }

            endpoints.Add(endpoint!);
            if (!string.Equals(value.AppId, appId, StringComparison.Ordinal))
            {
                continue;
            }

            var ttl = value.CacheTtlSeconds;
            if (servers.TryGetValue(value.Server, out var server))
            {
                if (server.Enabled == false || server.Auth == SandboxMcpAuthMode.None)
                {
                    continue;
                }

                if (
                    server.Url is not null
                    && (!TryCredentialEndpoint(server.Url, out var pinned) || pinned != endpoint)
                )
                {
                    throw new ArgumentException("MCP binding and policy URLs must agree.");
                }

                if (
                    server.Headers is { } headers
                    && headers.Any(h => !value.Headers.Contains(h, StringComparer.OrdinalIgnoreCase))
                )
                {
                    throw new ArgumentException("MCP policy headers must be permitted by the credential binding.");
                }

                ttl = Math.Min(ttl, server.CacheTtlSeconds ?? 300);
            }

            bindings.Add(
                value.Server,
                new SandboxMcpCredentialBinding(
                    value.Server,
                    endpoint!,
                    value.ProviderId,
                    Array.AsReadOnly(value.Headers.ToArray()),
                    ttl,
                    value.Plugin,
                    value.Marketplace
                )
            );
        }

        // Protect explicitly configured credential-capable servers even when this app has no binding.
        // Otherwise a generated legacy host route can bypass the MCP callback's missing-binding denial.
        foreach (var (name, server) in servers.ToArray())
        {
            if (server.Enabled == false || server.Auth == SandboxMcpAuthMode.None)
            {
                continue;
            }
            if (server.Url is { } url)
            {
                if (!TryCredentialEndpoint(url, out var endpoint))
                {
                    throw new ArgumentException(
                        "Credential-capable MCP servers require a canonical HTTPS endpoint URL pin."
                    );
                }
                endpoints.Add(endpoint!);
            }
            else if (bindings.TryGetValue(name, out var binding))
            {
                servers[name] = new SandboxMcpServerPolicy(
                    server.Auth,
                    server.Headers,
                    server.CacheTtlSeconds,
                    server.Enabled,
                    binding.Endpoint.OriginalString
                );
            }
            else
            {
                throw new ArgumentException(
                    "Credential-capable MCP servers require an endpoint URL pin or a matching binding endpoint for the creating app."
                );
            }
        }

        return new SandboxMcpSessionContext(
            appId,
            new SandboxMcpPolicy(egress, servers),
            new ReadOnlyDictionary<string, SandboxMcpCredentialBinding>(bindings),
            endpoints.AsReadOnly()
        );
    }

    internal static bool TryCredentialEndpoint(string? value, out Uri? endpoint) =>
        Uri.TryCreate(value, UriKind.Absolute, out endpoint)
        && endpoint.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(endpoint.UserInfo)
        && string.IsNullOrEmpty(endpoint.Query)
        && string.IsNullOrEmpty(endpoint.Fragment)
        && HasCanonicalPath(value, endpoint);

    private static bool HasCanonicalPath(string? value, Uri endpoint)
    {
        if (value is null || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || value.Contains('\\'))
        {
            return false;
        }
        var pathStart = value.IndexOf('/', "https://".Length);
        var rawPath = pathStart < 0 ? "/" : value[pathStart..];
        return string.Equals(rawPath, endpoint.AbsolutePath, StringComparison.Ordinal);
    }
}
