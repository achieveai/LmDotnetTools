using System.Net.Http.Json;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// Raw MCP JSON-RPC against the live gateway, because the SDK deliberately has no MCP client. Every live suite goes
/// through here, so a protocol fix reaches them all. Reads the same <c>SANDBOX_*</c> variables as the fixture.
/// </summary>
internal static class SandboxMcpRpc
{
    /// <summary>
    /// Sends one request as <paramref name="agentId"/>, or as the gateway's default agent when it is null. The caller
    /// owns the returned document; a JSON-RPC error throws instead.
    /// </summary>
    public static async Task<JsonDocument> SendAsync(
        string sessionId,
        string method,
        object parameters,
        string? agentId = null
    )
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(Environment.GetEnvironmentVariable("SANDBOX_BASE_URL")!.Trim()), "mcp")
        )
        {
            Content = JsonContent.Create(
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method,
                    @params = parameters,
                }
            ),
        };
        request.Headers.Add("X-Sbx-App-Id", Environment.GetEnvironmentVariable("SANDBOX_APP_ID")!.Trim());
        request.Headers.Add("X-Sbx-App-Key", Environment.GetEnvironmentVariable("SANDBOX_APP_KEY")!.Trim());
        request.Headers.Add("X-Session-ID", sessionId);
        request.Headers.Add("Accept", "application/json, text/event-stream");
        if (agentId is not null)
        {
            request.Headers.Add(SandboxAgentId.HeaderName, agentId);
        }

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (json.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.ToString();
            json.Dispose();
            throw new InvalidOperationException($"{method} JSON-RPC error: {message}");
        }

        return json;
    }

    /// <summary>Calls one tool and returns its text; a tool's own failure is <c>IsError</c>, not an exception.</summary>
    public static async Task<(bool IsError, string Text)> CallToolAsync(
        string sessionId,
        string? agentId,
        string tool,
        object arguments
    )
    {
        using var json = await SendAsync(sessionId, "tools/call", new { name = tool, arguments }, agentId);
        var result = json.RootElement.GetProperty("result");
        var text = string.Concat(
            result
                .GetProperty("content")
                .EnumerateArray()
                .Select(c => c.TryGetProperty("text", out var t) ? t.GetString() : "")
        );
        return (result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), text);
    }
}
