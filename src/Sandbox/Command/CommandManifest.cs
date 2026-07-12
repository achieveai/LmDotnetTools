using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>
/// The completion manifest a command wrapper persists after running the command, and the SDK reads
/// back to assemble (or recover) a <see cref="SandboxCommandResult"/>. It is metadata only — exit
/// code, per-stream length/digest, an optional small inline copy of each stream, the canonical
/// command digest, and the lease/creation timestamps — and deliberately carries <b>no</b> credential
/// or gateway-controlled free text, so persisting it under restrictive permissions leaks nothing.
/// </summary>
/// <remarks>
/// The shape is intentionally shallow and printable so the POSIX <c>sh</c> wrapper can emit it with a
/// single <c>printf</c>. It is the SDK's own artifact format (not a gateway contract), so the SDK
/// owns both ends of the (de)serialization via <see cref="Json"/>.
/// </remarks>
internal sealed record CommandManifest
{
    /// <summary>Current manifest schema version.</summary>
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>The canonical digest of the command that produced this manifest (see <see cref="CommandOperation.CanonicalDigest"/>).</summary>
    [JsonPropertyName("digest")]
    public string Digest { get; init; } = string.Empty;

    /// <summary>The command's process exit code.</summary>
    [JsonPropertyName("exit")]
    public int ExitCode { get; init; }

    [JsonPropertyName("stdout")]
    public CommandStreamManifest Stdout { get; init; } = new();

    [JsonPropertyName("stderr")]
    public CommandStreamManifest Stderr { get; init; } = new();

    /// <summary>Unix-seconds instant after which the operation's lease is considered expired (execution timeout + grace).</summary>
    [JsonPropertyName("lease")]
    public long LeaseUnixSeconds { get; init; }

    /// <summary>Unix-seconds instant the manifest was written.</summary>
    [JsonPropertyName("created")]
    public long CreatedUnixSeconds { get; init; }

    /// <summary>The single serializer used for both writing (fake/wrapper) and reading (SDK) manifests.</summary>
    public static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
}

/// <summary>Per-stream capture metadata: exact byte length, lowercase-hex SHA-256, and an optional inline base64 copy for small streams.</summary>
internal sealed record CommandStreamManifest
{
    /// <summary>Exact number of bytes the stream produced.</summary>
    [JsonPropertyName("len")]
    public long Length { get; init; }

    /// <summary>Lowercase-hex SHA-256 of the exact stream bytes, verified after reassembly.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>
    /// Base64 of the entire stream when it is small enough to inline (see
    /// <see cref="CommandArtifactLayout.InlineThresholdBytes"/>); <c>null</c> when the stream must be
    /// read back in chunks instead.
    /// </summary>
    [JsonPropertyName("inline")]
    public string? Inline { get; init; }
}
