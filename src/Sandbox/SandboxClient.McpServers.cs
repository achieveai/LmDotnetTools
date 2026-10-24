using System.Net.Http.Json;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>
    /// The MCP servers declared by the plugins <paramref name="sessionId"/> admitted, in full and as authored (gateway
    /// issue #224): rows are sorted by marketplace, plugin and name. The gateway does not proxy them; the caller
    /// connects itself. <c>${VAR}</c> references are returned unexpanded.
    /// </summary>
    /// <remarks>
    /// An unknown or foreign session is <see cref="SandboxErrorKind.NotFound"/> (as is a gateway without the route).
    /// <c>503 marketplaces_unavailable</c> is <see cref="SandboxErrorKind.Unavailable"/>: retry; the gateway never
    /// answers with a shorter list instead. SECRET: the rows can carry credentials (see <see cref="SandboxMcpServer"/>).
    /// </remarks>
    /// <param name="sessionId">The sandbox session.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<SandboxMcpServer>> GetMcpServersAsync(
        string sessionId,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var operation = $"listing the plugin MCP servers of session '{sessionId}'";
        using var response = await SendRestAsync(
                HttpMethod.Get,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/mcp-servers",
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, operation, ct).ConfigureAwait(false);
        }

        var statusCode = (int)response.StatusCode;
        McpServersResponseDto? payload;
        try
        {
            payload = await response
                .Content.ReadFromJsonAsync<McpServersResponseDto>(SandboxJson.RestOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a malformed MCP server list for session '{sessionId}'.",
                statusCode,
                ex
            );
        }

        if (payload?.McpServers is null)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned no MCP server list for session '{sessionId}'.",
                statusCode
            );
        }

        return SelectNonNullOrThrow(
            payload.McpServers,
            dto => ToMcpServer(dto, operation, statusCode),
            operation,
            statusCode
        );
    }

    private static SandboxMcpServer ToMcpServer(McpServerDto dto, string operation, int statusCode)
    {
        if (
            string.IsNullOrEmpty(dto.Name)
            || string.IsNullOrEmpty(dto.Plugin)
            || string.IsNullOrEmpty(dto.Marketplace)
            || string.IsNullOrEmpty(dto.Type)
            || dto.Supported is not { } supported
        )
        {
            // Names the missing field's row by nothing it carries: a row's values can be secret.
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned an MCP server without a name, plugin, marketplace, type or supported flag for {operation}.",
                statusCode
            );
        }

        return new SandboxMcpServer
        {
            Name = dto.Name,
            Plugin = dto.Plugin,
            Marketplace = dto.Marketplace,
            Type = dto.Type,
            Supported = supported,
            Reason = dto.Reason,
            Url = dto.Url,
            Headers = SelectNonNullOrThrow(
                dto.Headers,
                header => ToMcpHeader(header, operation, statusCode),
                operation,
                statusCode
            ),
            Extra =
                dto.Extra?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
                ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
        };
    }

    private static SandboxMcpHeader ToMcpHeader(McpHeaderDto dto, string operation, int statusCode) =>
        dto is { Name: { Length: > 0 } name, Value: { } value }
            ? new SandboxMcpHeader
            {
                Name = name,
                Value = value,
                Placeholder = dto.Placeholder,
            }
            : throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned an MCP server header without a name or value for {operation}.",
                statusCode
            );
}
