namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>
/// POSIX single-quoting for an ordered argv, so a caller-supplied argument vector can be embedded
/// verbatim into the <c>/bin/sh -c</c> command string the gateway's Bash tool runs (see
/// <c>crates/agent-cli/src/commands/exec.rs</c> at the pinned gateway commit — every command is
/// executed via <c>/bin/sh -c</c>, so the SDK must hand it a single, safely-quoted string, not an
/// argv array).
/// </summary>
/// <remarks>
/// <para>
/// Single-quoting is the only injection-proof POSIX quoting: inside a single-quoted string every
/// byte is literal except the single quote itself, which cannot be escaped inside single quotes and
/// is therefore emitted as the four-byte sequence <c>'\''</c> (close-quote, backslash-escaped
/// literal quote, reopen-quote). Nothing a caller can put in an argument — <c>$</c>, backticks,
/// <c>;</c>, <c>|</c>, <c>&amp;</c>, <c>&gt;</c>, newlines, globs — is interpreted, so a hostile
/// argument can never break out of its token or inject a second command.
/// </para>
/// <para>
/// A NUL byte cannot occur in a POSIX shell word (the kernel terminates <c>argv</c> strings at the
/// first NUL) and would silently truncate the command, so it is rejected outright rather than
/// quoted. The empty string is a legal argument and is rendered as an explicit empty quoted token
/// (<c>''</c>) so it survives as a distinct, present argument rather than vanishing.
/// </para>
/// </remarks>
internal static class PosixArgv
{
    /// <summary>The literal NUL byte, illegal in a POSIX shell word.</summary>
    private const char Nul = '\0';

    /// <summary>
    /// Single-quotes one argv token. Throws <see cref="ArgumentException"/> if <paramref name="token"/>
    /// contains a NUL byte (which cannot appear in a shell word). A <c>null</c> token is a programming
    /// error and throws <see cref="ArgumentNullException"/>.
    /// </summary>
    public static string Quote(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.IndexOf(Nul, StringComparison.Ordinal) >= 0)
        {
            throw new ArgumentException("A command argument may not contain a NUL byte.", nameof(token));
        }

        // '  ->  '\''  (end quote, escaped literal quote, reopen quote)
        return string.Concat("'", token.Replace("'", "'\\''", StringComparison.Ordinal), "'");
    }

    /// <summary>
    /// Renders an ordered argv as a single space-separated, fully-quoted command string suitable for
    /// embedding in a <c>/bin/sh -c</c> script. The vector must be non-empty (a command needs at
    /// least a program name) and contain no <c>null</c> element; each element is quoted via
    /// <see cref="Quote(string)"/>, so NUL bytes are rejected here too.
    /// </summary>
    public static string Join(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0)
        {
            throw new ArgumentException("A command must have at least one argument (the program name).", nameof(argv));
        }

        var quoted = new string[argv.Count];
        for (var i = 0; i < argv.Count; i++)
        {
            var token = argv[i] ?? throw new ArgumentException($"Command argument at index {i} is null.", nameof(argv));
            quoted[i] = Quote(token);
        }

        return string.Join(' ', quoted);
    }
}
