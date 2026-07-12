using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>
/// Derives the two stable identifiers a recoverable command execution turns on: the per-operation
/// artifact directory name and the versioned canonical digest of the command. Both are pure functions
/// of their inputs, so an original submission and any later recovery attempt for the same logical
/// operation compute byte-identical values.
/// </summary>
/// <remarks>
/// <para>
/// <b>The caller's operation id is never used as a path.</b> It is length-bounded and screened for
/// control characters (so it is safe to log), then hashed together with the session id into a
/// fixed-length hex directory name via <see cref="OperationDirectoryName"/>. This means a hostile or
/// awkward id (path separators, <c>..</c>, absurd length, homoglyph collisions across sessions)
/// cannot influence where artifacts land.
/// </para>
/// <para>
/// <b>The canonical digest is length-prefixed.</b> Every component (session id, each argv element,
/// the normalized working directory, the execution-timeout seconds) is written as a 4-byte
/// big-endian length followed by its bytes, under a version domain tag. Length-prefixing makes the
/// encoding unambiguous — no two distinct inputs can serialize to the same byte stream by shifting a
/// separator — so a later call that re-uses an operation id with even a subtly different command
/// produces a different digest and is refused rather than silently recovering the wrong result.
/// </para>
/// </remarks>
internal static class CommandOperation
{
    /// <summary>Maximum length of a caller-supplied operation id (characters).</summary>
    public const int MaxOperationIdLength = 128;

    /// <summary>Domain tag mixed into the operation-directory hash; bump if the derivation changes.</summary>
    private const string DirectoryDomainTag = "lmsbx-op-dir-v1";

    /// <summary>Domain tag mixed into the canonical command digest; bump if the digest inputs change.</summary>
    private const string DigestDomainTag = "lmsbx-cmd-digest-v1";

    /// <summary>Hex characters produced for the operation-directory name (16 bytes of SHA-256).</summary>
    private const int DirectoryNameHexLength = 32;

    /// <summary>
    /// Validates a caller-supplied operation id: non-empty, at most <see cref="MaxOperationIdLength"/>
    /// characters, and free of control characters (including NUL, so it stays log-safe). Throws
    /// <see cref="ArgumentException"/> otherwise. A <c>null</c> id is the caller opting out of an
    /// explicit id and is handled by <see cref="ResolveOperationId"/>, not here.
    /// </summary>
    public static void ValidateOperationId(string operationId, string paramName)
    {
        ArgumentNullException.ThrowIfNull(operationId, paramName);
        if (operationId.Length == 0)
        {
            throw new ArgumentException("Operation id must not be empty.", paramName);
        }

        if (operationId.Length > MaxOperationIdLength)
        {
            throw new ArgumentException(
                $"Operation id must be at most {MaxOperationIdLength} characters (was {operationId.Length}).",
                paramName
            );
        }

        foreach (var ch in operationId)
        {
            if (char.IsControl(ch))
            {
                throw new ArgumentException("Operation id must not contain control characters.", paramName);
            }
        }
    }

    /// <summary>
    /// Returns the caller's <paramref name="operationId"/> when supplied, otherwise a freshly generated
    /// one. A generated id is a GUID in <c>N</c> format (32 lowercase hex chars) — collision-resistant
    /// and inherently within all validation bounds; the caller reads it back from the result (or from a
    /// timeout exception) to recover the same operation later.
    /// </summary>
    public static string ResolveOperationId(string? operationId) => operationId ?? Guid.NewGuid().ToString("N");

    /// <summary>
    /// Computes the fixed-length, hex-only artifact directory name for <paramref name="operationId"/>
    /// within <paramref name="sessionId"/>. Binding the session id in keeps two sessions that happen to
    /// pick the same operation id in separate directories.
    /// </summary>
    public static string OperationDirectoryName(string sessionId, string operationId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthPrefixed(hash, DirectoryDomainTag);
        AppendLengthPrefixed(hash, sessionId);
        AppendLengthPrefixed(hash, operationId);
        return ToLowerHex(hash.GetHashAndReset())[..DirectoryNameHexLength];
    }

    /// <summary>
    /// Computes the versioned canonical digest binding the session id, the ordered argv, the normalized
    /// working directory, and the gateway execution-timeout seconds. Any change to any of these yields a
    /// different digest; recovery only proceeds when a persisted digest matches this value exactly.
    /// </summary>
    public static string CanonicalDigest(
        string sessionId,
        IReadOnlyList<string> arguments,
        string normalizedWorkingDirectory,
        long executionTimeoutSeconds
    )
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthPrefixed(hash, DigestDomainTag);
        AppendLengthPrefixed(hash, sessionId);
        AppendLengthPrefixed(hash, arguments.Count);
        foreach (var argument in arguments)
        {
            AppendLengthPrefixed(hash, argument);
        }

        AppendLengthPrefixed(hash, normalizedWorkingDirectory);
        AppendLengthPrefixed(hash, executionTimeoutSeconds);
        return ToLowerHex(hash.GetHashAndReset());
    }

    /// <summary>
    /// Lowercase hex of <paramref name="bytes"/>. Written in terms of <see cref="Convert.ToHexString(byte[])"/>
    /// + <see cref="string.ToLowerInvariant"/> rather than <c>Convert.ToHexStringLower</c>, which does not
    /// exist on the <c>net8.0</c> target framework.
    /// </summary>
    private static string ToLowerHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static void AppendLengthPrefixed(IncrementalHash hash, string value) =>
        AppendLengthPrefixed(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendLengthPrefixed(IncrementalHash hash, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        AppendLengthPrefixed(hash, buffer);
    }

    private static void AppendLengthPrefixed(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
