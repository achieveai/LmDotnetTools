using System.Text.Json;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    // Browser results carry base64 screenshots, so they need more room than ordinary REST JSON but must
    // still be bounded before parsing: a runaway gateway response is refused, not buffered whole.
    internal const long MaxBrowserResponseBytes = 32L * 1024 * 1024;

    /// <summary>Checks the gateway's advertised browser support without leasing a browser.</summary>
    public async Task<bool> SupportsBrowserAsync(CancellationToken ct = default)
    {
        using var response = await SendRestAsync(HttpMethod.Get, "mcp/tools", null, null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, "browser capability discovery", ct).ConfigureAwait(false);
        }
        var listing = await ReadBrowserJsonAsync(response, ct).ConfigureAwait(false);
        if (!listing.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            throw new SandboxException(SandboxErrorKind.Protocol, "Gateway tool listing is invalid.");
        }
        return tools
            .EnumerateArray()
            .Any(tool =>
                tool.ValueKind == JsonValueKind.Object
                && tool.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && name.GetString() == "Browser"
            );
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
            throw await MapErrorResponseAsync(response, "browser tool call", ct).ConfigureAwait(false);
        }
        var envelope = await ReadBrowserJsonAsync(response, ct).ConfigureAwait(false);
        return UnwrapJsonRpcResult(envelope, "browser tool call");
    }

    /// <summary>
    /// Returns a JSON-RPC response's <c>result</c> object, or throws for every other shape. The one place
    /// an MCP call in this SDK interprets an envelope, so every call speaks the same error contract:
    /// <list type="bullet">
    /// <item>An <c>error</c> object → <see cref="SandboxErrorKind.McpCallFailed"/> carrying its integer
    /// <c>code</c> on <see cref="SandboxException.JsonRpcCode"/>. Its <c>message</c> is never copied into the
    /// exception: it is gateway-controlled text that can echo tool output or credentials (ADR 0001).</item>
    /// <item>Neither an <c>error</c> nor an object <c>result</c> → <see cref="SandboxErrorKind.Protocol"/>.</item>
    /// </list>
    /// </summary>
    private static JsonElement UnwrapJsonRpcResult(JsonElement envelope, string operation)
    {
        if (envelope.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            int? code =
                error.TryGetProperty("code", out var codeElement)
                && codeElement.ValueKind == JsonValueKind.Number
                && codeElement.TryGetInt32(out var parsedCode)
                    ? parsedCode
                    : null;
            var codeText = code is { } value ? $" {value}" : string.Empty;
            throw new SandboxException(
                SandboxErrorKind.McpCallFailed,
                $"Sandbox gateway {operation} returned JSON-RPC error{codeText}."
            )
            {
                JsonRpcCode = code,
            };
        }

        if (!envelope.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            throw new SandboxException(SandboxErrorKind.Protocol, $"Gateway {operation} returned no MCP result.");
        }

        return result.Clone();
    }

    /// <summary>
    /// Destroys the browser bound to <paramref name="browserId"/> under this client's app, now. Idempotent:
    /// the gateway answers <c>204</c> whether or not a browser was bound, so closing twice succeeds twice.
    /// The next request for the same id binds a fresh browser.
    /// </summary>
    /// <exception cref="SandboxException">
    /// <see cref="SandboxErrorKind.BrowserUnsupported"/> when the gateway does not enable browsers (its only
    /// <c>404</c> on this route), or the usual transport/authorization kinds.
    /// </exception>
    public async Task CloseBrowserAsync(string browserId, CancellationToken ct = default)
    {
        ValidateBrowserId(browserId);
        using var response = await SendRestAsync(HttpMethod.Delete, BrowserPath(browserId), null, null, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, "browser close", ct).ConfigureAwait(false);
        }
    }

    private static string BrowserPath(string browserId, string suffix = "") =>
        $"api/v1/browsers/{Uri.EscapeDataString(browserId)}{suffix}";

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
        var body = await ReadStreamCappedAsync(response, "browser response", null, MaxBrowserResponseBytes, ct)
            .ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(body);
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
