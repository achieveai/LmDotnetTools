namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>
/// Structurally validates a decoded <see cref="CommandManifest"/> BEFORE the SDK materializes any
/// stream from it. A manifest is the SDK's own artifact, but it is read back through the gateway from a
/// file the SDK cannot fully trust (it could be truncated, tampered with, or written by a mismatched
/// version), so every field is checked here and any violation is surfaced as a
/// <see cref="SandboxException"/> with <see cref="SandboxErrorKind.Protocol"/>. This guarantees a
/// malformed manifest can never reach materialization and surface as a raw
/// <see cref="System.FormatException"/>/<see cref="System.NullReferenceException"/> or a confusing
/// downstream failure — it always maps to the SDK's stable error contract.
/// </summary>
internal static class CommandManifestValidator
{
    /// <summary>Length, in characters, of a lowercase-hex SHA-256 digest.</summary>
    private const int Sha256HexLength = 64;

    /// <summary>Length, in characters, of the lowercase-hex per-execution generation identifier.</summary>
    private const int GenerationHexLength = CommandArtifactLayout.OperationDirectoryNameLength;

    /// <summary>
    /// Validates <paramref name="manifest"/> in full: schema version, digest shape, execution-generation
    /// shape, non-null stream records, each stream's non-negative length and well-formed digest, and
    /// non-negative timestamps. Throws <see cref="SandboxException"/> (<see cref="SandboxErrorKind.Protocol"/>)
    /// — never a raw exception — on the first violation. The digest's VALUE match against the expected
    /// command digest is a separate integrity check performed by the caller after this structural pass.
    /// </summary>
    public static void Validate(CommandManifest manifest, string operationId)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Version != CommandManifest.CurrentVersion)
        {
            throw Malformed("its schema version is unsupported", operationId);
        }

        if (!IsLowercaseHex(manifest.Digest, Sha256HexLength))
        {
            throw Malformed("its command digest is not a 64-character lowercase-hex value", operationId);
        }

        if (!IsLowercaseHex(manifest.Generation, GenerationHexLength))
        {
            throw Malformed("its execution generation is not a 32-character lowercase-hex value", operationId);
        }

        if (manifest.Stdout is null || manifest.Stderr is null)
        {
            throw Malformed("a required stream record is missing", operationId);
        }

        ValidateStream(manifest.Stdout, "stdout", operationId);
        ValidateStream(manifest.Stderr, "stderr", operationId);

        if (manifest.LeaseUnixSeconds < 0 || manifest.CreatedUnixSeconds < 0)
        {
            throw Malformed("a timestamp is negative", operationId);
        }
    }

    private static void ValidateStream(CommandStreamManifest stream, string streamName, string operationId)
    {
        if (stream.Length < 0)
        {
            throw Malformed($"the {streamName} length is negative", operationId);
        }

        if (!IsLowercaseHex(stream.Sha256, Sha256HexLength))
        {
            throw Malformed($"the {streamName} digest is not a 64-character lowercase-hex value", operationId);
        }

        // Inline is intentionally NOT decoded here: it is null for a chunked stream, and for an inlined
        // stream it is decoded and verified against this length + digest during materialization, where a
        // malformed value already maps to Protocol/Integrity.
    }

    private static bool IsLowercaseHex(string? value, int length)
    {
        if (value is null || value.Length != length)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch is (< '0' or > '9') and (< 'a' or > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the fixed-shape Protocol failure. The <paramref name="reason"/> is an SDK-authored,
    /// gateway-independent phrase — it never interpolates any value read from the manifest — so the
    /// message stays safe to surface while still being specific enough to diagnose.
    /// </summary>
    private static SandboxException Malformed(string reason, string operationId) =>
        new(
            SandboxErrorKind.Protocol,
            $"Sandbox command manifest failed validation: {reason}.",
            operationId: operationId
        );
}
