using System.Net;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>Checks the gateway's advertised browser support without leasing a browser.</summary>
    public async Task<bool> SupportsBrowserAsync(CancellationToken ct = default)
    {
        using var response = await SendRestAsync(HttpMethod.Get, "mcp/tools", null, null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapErrorResponse(response, "browser capability discovery");
        }
        var listing = await ReadBrowserJsonAsync(response, ct).ConfigureAwait(false);
        if (!listing.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            throw new SandboxException(SandboxErrorKind.Protocol, "Gateway tool listing is invalid.");
        }
        return tools
            .EnumerateArray()
            .Any(tool => tool.TryGetProperty("name", out var name) && name.GetString() == "Browser");
    }

    /// <summary>Discovers or calls a leased browser's MCP tools, preserving images and instance metadata.</summary>
    public async Task<JsonElement> CallBrowserAsync(
        string browserId,
        string toolkit,
        string? toolName = null,
        JsonElement? toolArguments = null,
        CancellationToken ct = default
    )
    {
        ValidateBrowserId(browserId);
        if (toolkit is not ("playwright" or "devtools"))
        {
            throw new ArgumentException("Toolkit must be playwright or devtools.", nameof(toolkit));
        }
        if (toolArguments is { ValueKind: not JsonValueKind.Object })
        {
            throw new ArgumentException("Tool arguments must be a JSON object.", nameof(toolArguments));
        }
        var arguments = new Dictionary<string, object?> { ["browser_id"] = browserId, ["server"] = toolkit };
        if (toolName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
            arguments["tool"] = toolName;
            arguments["arguments"] = toolArguments;
        }
        var payload = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "Browser", arguments },
        };
        using var response = await SendRestAsync(HttpMethod.Post, "mcp", payload, null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapErrorResponse(response, "browser tool call");
        }
        var envelope = await ReadBrowserJsonAsync(response, ct).ConfigureAwait(false);
        if (!envelope.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            throw new SandboxException(SandboxErrorKind.Protocol, "Gateway browser call returned no MCP result.");
        }
        return result.Clone();
    }

    /// <summary>Closes only the browser lease under this client's credential; false means already absent.</summary>
    public async Task<bool> CloseBrowserAsync(string browserId, CancellationToken ct = default)
    {
        ValidateBrowserId(browserId);
        using var response = await SendRestAsync(
                HttpMethod.Delete,
                $"api/v1/browsers/{Uri.EscapeDataString(browserId)}",
                null,
                null,
                ct
            )
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw MapErrorResponse(response, "browser close");
        }
        return true;
    }

    private static void ValidateBrowserId(string browserId)
    {
        if (
            browserId is not { Length: > 0 and <= 128 }
            || !browserId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')
        )
        {
            throw new ArgumentException(
                "Browser ID must contain 1–128 letters, digits, dots, underscores, colons or hyphens.",
                nameof(browserId)
            );
        }
    }

    private static async Task<JsonElement> ReadBrowserJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }
            return json.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Gateway browser response is invalid JSON.",
                innerException: ex
            );
        }
    }
}
