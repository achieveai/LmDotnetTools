namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>
/// Validates, canonicalizes, and generates the operation id a recoverable command execution turns on.
/// The id is the gateway's idempotency key for the operations API (ADR 0031 / issue #119): resubmitting
/// the same id with an identical request replays the existing operation rather than running it again. It
/// is validated against the gateway's <c>validate_operation_id</c> grammar EXACTLY (trim, then non-empty,
/// at most <see cref="MaxOperationIdLength"/> chars, not <c>.</c> or <c>..</c>, and only ASCII
/// <c>[A-Za-z0-9._-]</c>) and the trimmed canonical form is stored/submitted — the gateway trims before
/// comparing, so canonicalizing locally keeps "what we submit == what we correlate == what the gateway
/// returns". The gateway (not this SDK) owns the request fingerprint that decides whether a same-id
/// resubmit is an identical replay or a <c>409 idempotency_conflict</c>, so there is no local digest here.
/// </summary>
internal static class CommandOperation
{
    /// <summary>
    /// Maximum length of a caller-supplied operation id (characters), matching the gateway's
    /// <c>validate_operation_id</c> cap. Checked against the TRIMMED value.
    /// </summary>
    public const int MaxOperationIdLength = 128;

    /// <summary>
    /// Validates a caller-supplied operation id against the gateway's <c>validate_operation_id</c> grammar
    /// and returns its CANONICAL (trimmed) form: surrounding whitespace is stripped, then the result must
    /// be non-empty, at most <see cref="MaxOperationIdLength"/> characters, not <c>.</c> or <c>..</c>, and
    /// composed only of ASCII letters, digits, <c>.</c>, <c>_</c>, and <c>-</c> (mirroring the gateway
    /// exactly, so an id that would pass here but fail remotely — or trim to a value the gateway returns
    /// and we then fail to correlate — is rejected up front). Throws <see cref="ArgumentException"/>
    /// otherwise. A <c>null</c> id is the caller opting out and is handled by
    /// <see cref="ResolveOperationId"/>, not here.
    /// </summary>
    public static string ValidateAndCanonicalizeOperationId(string operationId, string paramName)
    {
        ArgumentNullException.ThrowIfNull(operationId, paramName);

        // The gateway trims before validating and RETURNS the trimmed value, so we canonicalize to the same
        // trimmed form — otherwise a submitted "  op  " would be echoed back as "op" and fail correlation.
        var canonical = operationId.Trim();

        if (canonical.Length == 0)
        {
            throw new ArgumentException("Operation id must not be empty (after trimming surrounding whitespace).", paramName);
        }

        if (canonical.Length > MaxOperationIdLength)
        {
            throw new ArgumentException(
                $"Operation id must be at most {MaxOperationIdLength} characters (was {canonical.Length}).",
                paramName
            );
        }

        if (canonical is "." or "..")
        {
            throw new ArgumentException("Operation id must not be '.' or '..'.", paramName);
        }

        foreach (var ch in canonical)
        {
            if (!IsAllowedOperationIdChar(ch))
            {
                throw new ArgumentException(
                    "Operation id may contain only ASCII letters, digits, '.', '_', and '-'.",
                    paramName
                );
            }
        }

        return canonical;
    }

    /// <summary>The gateway's allowed operation-id character class: ASCII <c>[A-Za-z0-9._-]</c>.</summary>
    private static bool IsAllowedOperationIdChar(char ch) =>
        ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';

    /// <summary>
    /// Returns the caller's <paramref name="operationId"/> when supplied (already validated/canonicalized
    /// by <see cref="SandboxCommand"/>), otherwise a freshly generated one. A generated id is a GUID in
    /// <c>N</c> format (32 lowercase hex chars) — collision-resistant and inherently within the grammar;
    /// the caller reads it back from the result (or a timeout exception) to recover the same operation.
    /// </summary>
    public static string ResolveOperationId(string? operationId) => operationId ?? Guid.NewGuid().ToString("N");
}
