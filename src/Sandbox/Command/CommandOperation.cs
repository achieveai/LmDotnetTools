namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>
/// Validates and generates the operation id a recoverable command execution turns on. The id is the
/// gateway's idempotency key for the operations API (ADR 0031 / issue #119): resubmitting the same
/// id with an identical request replays the existing operation rather than running it again. It is
/// bounded and screened for control characters so it stays log-safe, and the gateway (not this SDK)
/// owns the request fingerprint that decides whether a same-id resubmit is an identical replay or a
/// <c>409 idempotency_conflict</c> — so there is no local digest to compute here.
/// </summary>
internal static class CommandOperation
{
    /// <summary>
    /// Maximum length of a caller-supplied operation id (characters). The gateway independently caps
    /// the id (it becomes a workspace directory component there); this local bound fails an oversized
    /// id fast, before any request is sent.
    /// </summary>
    public const int MaxOperationIdLength = 128;

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
}
