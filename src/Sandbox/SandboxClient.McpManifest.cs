using System.Net.Http.Json;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>Reads the library manifest, distinct from authored MCP server definitions.</summary>
    /// <remarks>Values may contain sensitive configuration. A successful read does not prove in-sandbox file synchronization.
    /// Unsupported routes and unavailable historical manifests remain NotFound; inspect SandboxException.ErrorCode.</remarks>
    public async Task<SandboxMcpManifest> GetMcpManifestAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using var response = await SendRestAsync(
                HttpMethod.Get,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/mcp-servers/manifest",
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, "reading the MCP manifest", ct).ConfigureAwait(false);
        }
        var status = (int)response.StatusCode;
        McpManifestDto? dto;
        try
        {
            dto = await response
                .Content.ReadFromJsonAsync<McpManifestDto>(SandboxJson.RestOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // JSON diagnostics can include credential-bearing response values. Do not retain the original exception.
            throw McpResponseError(status);
        }
        catch (InvalidOperationException)
        {
            // HttpContent rejects unsupported charset declarations before JSON parsing.
            throw McpResponseError(status);
        }
        if (dto is null || dto.Schema != "sbx.mcp-servers/1" || dto.SessionId != sessionId)
        {
            throw McpResponseError(status);
        }
        return new SandboxMcpManifest(
            dto.Schema,
            dto.SessionId,
            McpRows(dto.Servers, row => ToMcpManifestServer(row, status), status)
        );
    }

    private static SandboxException McpResponseError(int status) =>
        new(SandboxErrorKind.Protocol, "Sandbox gateway returned malformed MCP response data.", status);

    private static string McpRequired(string? value, int status) =>
        string.IsNullOrWhiteSpace(value) ? throw McpResponseError(status) : value;

    private static IReadOnlyList<TOut> McpRows<TIn, TOut>(IReadOnlyList<TIn?>? rows, Func<TIn, TOut> map, int status)
        where TIn : class
    {
        if (rows is null)
        {
            throw McpResponseError(status);
        }
        var result = new TOut[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            result[i] = rows[i] is { } row ? map(row) : throw McpResponseError(status);
        }
        return Array.AsReadOnly(result);
    }

    private static IReadOnlyList<string> McpNames(IReadOnlyList<string?>? values, int status) =>
        McpRows(values, value => value, status);

    private static SandboxMcpAuthMode McpAuth(string? value, int status) =>
        value switch
        {
            "none" => SandboxMcpAuthMode.None,
            "injected" => SandboxMcpAuthMode.Injected,
            _ => throw McpResponseError(status),
        };

    private static SandboxMcpResolvedPolicy ToMcpResolvedPolicy(McpResolvedPolicyDto dto, int status) =>
        new(
            dto.Egress switch
            {
                "auto" => SandboxMcpEgress.Auto,
                "manual" => SandboxMcpEgress.Manual,
                _ => throw McpResponseError(status),
            },
            McpRows(
                dto.Servers,
                row => new SandboxMcpResolvedServer(
                    McpRequired(row.Name, status),
                    McpRequired(row.Plugin, status),
                    McpRequired(row.Marketplace, status),
                    McpRequired(row.Transport, status),
                    row.Url,
                    McpAuth(row.Auth, status),
                    McpNames(row.Headers, status),
                    row.ProviderId,
                    row.RuleId,
                    McpNames(row.UnresolvedVars, status)
                ),
                status
            )
        );

    private static SandboxMcpManifestServer ToMcpManifestServer(McpManifestServerDto row, int status)
    {
        if (row.Headers is null || row.Auth is null || row.Headers.Values.Any(value => value is null))
        {
            throw McpResponseError(status);
        }
        var transport = McpRequired(row.Transport, status);
        var supported = row.Supported ?? (transport == "http" ? true : throw McpResponseError(status));
        var auth = new SandboxMcpManifestAuth(
            McpAuth(row.Auth.Mode, status),
            row.Auth.Provider,
            McpNames(row.Auth.Headers, status)
        );
        var egress = row.Egress switch
        {
            "auto" => SandboxMcpManifestEgress.Auto,
            "manual" => SandboxMcpManifestEgress.Manual,
            "stale" => SandboxMcpManifestEgress.Stale,
            _ => throw McpResponseError(status),
        };
        var generated = row.Generated is { } g
            ? new SandboxMcpManifestGenerated(
                McpRequired(g.Plugin, status),
                McpRequired(g.Marketplace, status),
                g.Skill,
                g.ToolsHash,
                McpRequired(g.LibPath, status)
            )
            : null;
        return new SandboxMcpManifestServer(
            McpRequired(row.Name, status),
            McpRequired(row.Plugin, status),
            McpRequired(row.Marketplace, status),
            transport,
            supported,
            row.UnsupportedReason,
            row.Url,
            row.UrlTemplate,
            row.Headers.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal),
            auth,
            egress,
            row.Note,
            McpNames(row.UnresolvedVars, status),
            generated
        );
    }
}
