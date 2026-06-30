using System.Text;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// Production <see cref="ISandboxFileSystem"/> over the gateway's single <c>Bash</c> tool
/// (<see cref="ISandboxCommandRunner"/>). Files cross the boundary <b>base64-encoded</b> so arbitrary
/// bytes (CRLF, UTF-8, NUL) survive intact and no file content is ever interpolated into a shell line:
/// reads run <c>base64 &lt;path&gt;</c> and decode the stdout in-process; writes <c>printf</c> the
/// base64 payload through <c>base64 -d</c> into the target after <c>mkdir -p</c> of its parent. A read
/// of a missing file fails the command and is reported as <c>null</c> (not an exception), matching the
/// interface contract.
/// </summary>
internal sealed class SandboxFileSystem : ISandboxFileSystem
{
    private readonly ISandboxCommandRunner _runner;

    public SandboxFileSystem(ISandboxCommandRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var result = await _runner
            .RunAsync(new SandboxCommand(["base64", path]), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null; // Missing/unreadable file — the contract is null, not throw.
        }

        var bytes = Convert.FromBase64String(StripWhitespace(result.Stdout));
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task WriteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var directory = ParentDirectory(path);

        // mkdir -p <dir> && printf %s '<base64>' | base64 -d > <path> — content is base64, never raw.
        var script =
            $"mkdir -p {PosixShell.Quote(directory)} && "
            + $"printf %s {PosixShell.Quote(payload)} | base64 -d > {PosixShell.Quote(path)}";

        var result = await _runner
            .RunAsync(new SandboxCommand(["sh", "-lc", script]), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new IOException(
                $"Sandbox write of '{path}' failed (exit {result.ExitCode}): {result.Stderr}");
        }
    }

    private static string ParentDirectory(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "." : path[..slash];
    }

    private static string StripWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch))
            {
                _ = builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
