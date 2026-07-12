using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.Sandbox.Wire;

/// <summary>
/// The two JSON contracts a <see cref="SandboxClient"/> speaks: the gateway's REST endpoints (snake_case
/// fields, nulls omitted) and its MCP JSON-RPC endpoint (fields verbatim, per the JSON-RPC/MCP spec —
/// <c>jsonrpc</c>/<c>method</c>/<c>params</c>/etc. are already lower-case single words, so no naming
/// policy is applied). Kept as two distinct <see cref="JsonSerializerOptions"/> instances rather than one
/// shared instance so a future REST-only or MCP-only change never risks the other wire shape.
/// </summary>
/// <remarks>
/// Neither instance sets <see cref="JsonSerializerOptions.UnmappedMemberHandling"/>, so both default to
/// silently ignoring unknown members on read — the gateway may add fields the SDK does not yet model
/// without breaking deserialization.
/// </remarks>
internal static class SandboxJson
{
    public static readonly JsonSerializerOptions RestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions McpOptions = new();
}
