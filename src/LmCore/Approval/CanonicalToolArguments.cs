using System.Security.Cryptography;
using System.Text;

namespace AchieveAi.LmDotnetTools.LmCore.Approval;

/// <summary>
/// The exact argument text a tool handler will be given, captured once so that what an approver
/// decided on and what actually runs cannot diverge.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Canonical" here means "the literal bytes that execute" — not normalized JSON.</b> Nothing in
/// this pipeline sorts keys, re-serializes, or rewrites whitespace: arguments are raw provider text
/// carried as a <see cref="string"/> from <c>ToolCall.FunctionArgs</c> to the handler. Two requests
/// that are semantically the same JSON but differ by a space are therefore different canonical
/// arguments with different hashes. That is the intended reading — the hash identifies an exact
/// invocation, not an equivalence class of them.
/// </para>
/// <para>
/// Capturing at gate-open is what closes the time-of-check-to-time-of-use gap. The caller may own
/// and mutate the <c>ToolCall</c> this was read from; once frozen here, the approved text is
/// unaffected, and <see cref="Json"/> is what the handler receives.
/// </para>
/// </remarks>
public sealed class CanonicalToolArguments
{
    private string? _sha256Hex;

    private CanonicalToolArguments(string json) => Json = json;

    /// <summary>
    /// The exact argument text handed to the handler. Never null — an absent or empty argument
    /// string is normalized to <c>{}</c>, matching what the executor has always passed through.
    /// </summary>
    public string Json { get; }

    /// <summary>
    /// SHA-256 of <see cref="Json"/> over its UTF-8 bytes, rendered as lowercase hex.
    /// </summary>
    /// <remarks>
    /// Computed on first read and cached, so a host that configures no policy and no gate never
    /// pays for a hash it does not use.
    /// </remarks>
    public string Sha256Hex => _sha256Hex ??= ComputeSha256Hex(Json);

    /// <summary>
    /// Captures <paramref name="argumentsJson"/> as the arguments that will execute.
    /// </summary>
    /// <param name="argumentsJson">
    /// Raw argument text from the tool call. Null or empty becomes <c>{}</c>.
    /// </param>
    public static CanonicalToolArguments Freeze(string? argumentsJson) =>
        new(string.IsNullOrEmpty(argumentsJson) ? "{}" : argumentsJson);

    private static string ComputeSha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(hash);
#else
        return Convert.ToHexString(hash).ToLowerInvariant();
#endif
    }
}
