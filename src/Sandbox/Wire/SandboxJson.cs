using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.Sandbox.Wire;

/// <summary>
/// The JSON contract a <see cref="SandboxClient"/> speaks to the gateway's REST endpoints: snake_case
/// field names, nulls omitted on write. Direct file/command API bodies (ADR 0031 / issue #119) use the
/// same options — their DTOs carry explicit <c>[JsonPropertyName]</c> attributes, which take precedence
/// over the naming policy, so a field like <c>operation_id</c> serializes identically either way.
/// </summary>
/// <remarks>
/// <see cref="JsonSerializerOptions.UnmappedMemberHandling"/> is left at its default, so unknown members
/// are silently ignored on read — the gateway may add fields the SDK does not yet model without breaking
/// deserialization.
/// </remarks>
internal static class SandboxJson
{
    public static readonly JsonSerializerOptions RestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
