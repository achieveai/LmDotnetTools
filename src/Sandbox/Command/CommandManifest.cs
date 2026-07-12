using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>
/// The completion manifest a command wrapper persists after running the command, and the SDK reads
/// back to assemble (or recover) a <see cref="SandboxCommandResult"/>. It is metadata only — exit
/// code, per-stream length/digest, an optional small inline copy of each stream, the canonical
/// command digest, an immutable per-execution generation id, and the lease/creation timestamps — and
/// deliberately carries <b>no</b> credential or gateway-controlled free text, so persisting it under
/// restrictive permissions leaks nothing.
/// </summary>
/// <remarks>
/// <para>
/// The shape is intentionally shallow and printable so the POSIX <c>sh</c> wrapper can emit it with a
/// single <c>printf</c>. It is the SDK's own artifact format (not a gateway contract), so the SDK
/// owns both ends of the (de)serialization via <see cref="Json"/>.
/// </para>
/// <para>
/// <b>Every field is <see cref="JsonRequiredAttribute">required</see> on the wire.</b> The wrapper's
/// <c>printf</c> always emits all of them (an inlined stream carries a base64 string, a chunked one an
/// explicit <c>null</c>), so a manifest the SDK reads back through the gateway that is missing ANY field
/// is truncated, tampered with, or version-mismatched — never a legitimate artifact. A missing field
/// therefore fails deserialization with a <see cref="JsonException"/> that
/// <see cref="SandboxClient"/> maps to <see cref="SandboxErrorKind.Protocol"/>, rather than silently
/// defaulting (a zero exit code, an empty digest, a zero length) and materializing a plausible-looking
/// but wrong result. Present-but-<c>null</c> values are caught the same way — value-typed fields fail
/// deserialization, reference-typed ones fail <see cref="CommandManifestValidator"/> — so no defect can
/// reach materialization as a successful default.
/// </para>
/// </remarks>
internal sealed record CommandManifest
{
    /// <summary>Current manifest schema version.</summary>
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")]
    [JsonRequired]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>The canonical digest of the command that produced this manifest (see <see cref="CommandOperation.CanonicalDigest"/>).</summary>
    [JsonPropertyName("digest")]
    [JsonRequired]
    public string Digest { get; init; } = string.Empty;

    /// <summary>
    /// The immutable per-execution generation identifier (32 lowercase-hex characters) assigned when the
    /// command was claimed and run. Because the artifact directory name is derived from the session and
    /// operation id only (not the command), the SAME directory can be reused across executions after the
    /// retention window elapses; the generation distinguishes those executions so a delayed output reclaim
    /// issued by an expired old execution can re-read it under the GC lock and refuse to delete a NEWER
    /// re-execution's output. It is written once and never mutated for a given execution.
    /// </summary>
    [JsonPropertyName("gen")]
    [JsonRequired]
    public string Generation { get; init; } = string.Empty;

    /// <summary>The command's process exit code.</summary>
    [JsonPropertyName("exit")]
    [JsonRequired]
    public int ExitCode { get; init; }

    [JsonPropertyName("stdout")]
    [JsonRequired]
    public CommandStreamManifest Stdout { get; init; } = new();

    [JsonPropertyName("stderr")]
    [JsonRequired]
    public CommandStreamManifest Stderr { get; init; } = new();

    /// <summary>Unix-seconds instant after which the operation's lease is considered expired (execution timeout + grace).</summary>
    [JsonPropertyName("lease")]
    [JsonRequired]
    public long LeaseUnixSeconds { get; init; }

    /// <summary>Unix-seconds instant the manifest was written.</summary>
    [JsonPropertyName("created")]
    [JsonRequired]
    public long CreatedUnixSeconds { get; init; }

    /// <summary>The single serializer used for both writing (fake/wrapper) and reading (SDK) manifests.</summary>
    public static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
}

/// <summary>Per-stream capture metadata: exact byte length, lowercase-hex SHA-256, and an optional inline base64 copy for small streams.</summary>
/// <remarks>
/// All three fields are <see cref="JsonRequiredAttribute">required</see> on the wire: the wrapper always
/// emits <c>len</c>, <c>sha256</c>, and <c>inline</c> (the last as a base64 string for a small stream or
/// an explicit <c>null</c> for a chunked one), so a missing sub-field is a truncated/tampered manifest
/// that must fail as <see cref="SandboxErrorKind.Protocol"/> rather than default to a zero length,
/// empty digest, or an unintended chunked read.
/// </remarks>
internal sealed record CommandStreamManifest
{
    /// <summary>Exact number of bytes the stream produced.</summary>
    [JsonPropertyName("len")]
    [JsonRequired]
    public long Length { get; init; }

    /// <summary>Lowercase-hex SHA-256 of the exact stream bytes, verified after reassembly.</summary>
    [JsonPropertyName("sha256")]
    [JsonRequired]
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>
    /// Base64 of the entire stream when it is small enough to inline (see
    /// <see cref="CommandArtifactLayout.InlineThresholdBytes"/>); <c>null</c> when the stream must be
    /// read back in chunks instead. The property itself is required to be PRESENT on the wire (as a
    /// string or an explicit <c>null</c>); only its value is optional.
    /// </summary>
    [JsonPropertyName("inline")]
    [JsonRequired]
    public string? Inline { get; init; }
}
