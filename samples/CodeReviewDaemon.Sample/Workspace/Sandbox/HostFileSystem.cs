using System.Text;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>Host-process <see cref="ISandboxFileSystem"/> for the daemon's retention checkout (design §6).</summary>
internal sealed class HostFileSystem : ISandboxFileSystem
{
    /// <summary>
    /// Fills <paramref name="maxBytes"/> + 1 and refuses if that last byte arrives, rather than asking
    /// <see cref="FileInfo.Length"/> first. A length check is a decision about a file taken before the file
    /// is read: the retention checkout is written by git and by the agent's own commits, so a file can grow
    /// between the two calls and the read that follows a passing check is unbounded again. Filling one byte
    /// past the ceiling answers the only question that matters — "is there more than I agreed to take?" —
    /// out of the same bytes it would have had to read anyway.
    /// </summary>
    public async Task<SandboxFileRead> ReadFileAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (!File.Exists(path))
        {
            return SandboxFileRead.Missing;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true
        );

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return SandboxFileRead.Refused;
            }

            buffer.Write(chunk, 0, read);
        }

        // Decoded exactly as File.ReadAllTextAsync did: UTF-8 with byte-order-mark detection and the
        // replacement-character fallback. The bound is the only thing this method changes; a read that used
        // to succeed on odd bytes still succeeds, so nothing downstream has to learn a new failure.
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return SandboxFileRead.Of(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task WriteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        IReadOnlyList<string> names = [.. Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName)!];
        return Task.FromResult(names);
    }
}
