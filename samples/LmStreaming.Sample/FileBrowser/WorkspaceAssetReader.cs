using System.Globalization;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.FileBrowser;

/// <summary>Reads HTML assets through no-follow directory/file handles inside the Linux sandbox.</summary>
internal static class WorkspaceAssetReader
{
    // The direct file API confines the workspace mount but follows links inside it. Use the
    // existing byte-exact command protocol to keep a narrower asset root pinned during the read.
    // Python is part of the gateway sandbox image; isolated mode ignores workspace import paths.
    internal const string ReadScript = """
        import errno, os, stat, sys
        root, relative, limit = sys.argv[1], sys.argv[2], int(sys.argv[3])
        def parts(value, empty=False):
            if empty and value == '': return []
            result = value.split('/')
            if any(p in ('', '.', '..') or '\\' in p or '\x00' in p for p in result):
                raise ValueError('invalid path')
            return result
        handles = []
        try:
            directory_flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
            current = os.open('/workspace', directory_flags)
            handles.append(current)
            for part in parts(root, True) + parts(relative)[:-1]:
                current = os.open(part, directory_flags, dir_fd=current)
                handles.append(current)
            file = os.open(parts(relative)[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=current)
            handles.append(file)
            info = os.fstat(file)
            if not stat.S_ISREG(info.st_mode): sys.exit(44)
            if info.st_size > limit: sys.exit(45)
            total = 0
            while True:
                chunk = os.read(file, min(65536, limit - total + 1))
                if not chunk: break
                total += len(chunk)
                if total > limit: sys.exit(45)
                sys.stdout.buffer.write(chunk)
            sys.stdout.buffer.flush()
        except OSError as error:
            sys.exit(44 if error.errno in (errno.ENOENT, errno.ENOTDIR, errno.ELOOP, errno.EACCES) else 46)
        except ValueError:
            sys.exit(44)
        finally:
            for handle in reversed(handles): os.close(handle)
        """;

    internal static async Task<(int Status, byte[] Bytes)> ReadAsync(
        IWorkspaceFileBrowser files,
        string sessionId,
        string root,
        string relativePath,
        long maxBytes,
        CancellationToken ct
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        if (maxBytes > FileBrowserLimits.MaxDownloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var command = new SandboxCommand([
            "python3",
            "-I",
            "-S",
            "-c",
            ReadScript,
            root,
            relativePath,
            maxBytes.ToString(CultureInfo.InvariantCulture),
        ])
        {
            // Absolute system executables are rejected by the native operation runner.
            // Resolve only from the immutable image directory, never session/workspace PATH.
            Environment = new Dictionary<string, string> { ["PATH"] = "/usr/local/bin" },
            MaxOutputBytes = maxBytes + 4096,
            ExecutionTimeout = TimeSpan.FromSeconds(15),
        };
        SandboxCommandBytesResult result;
        try
        {
            result = await files.ExecuteWorkspaceCommandBytesAsync(sessionId, command, timeout.Token);
        }
        catch (SandboxException error) when (error.Kind == SandboxErrorKind.OutputLimitExceeded)
        {
            return (413, []);
        }
        catch (SandboxException error) when (error.Kind == SandboxErrorKind.ExecutionTimeout)
        {
            return (504, []);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (504, []);
        }
        if (result.ExitCode != 0)
        {
            return result.ExitCode switch
            {
                44 => (404, []),
                45 => (413, []),
                _ => (502, []),
            };
        }
        return result.StandardOutput.LongLength <= maxBytes ? (200, result.StandardOutput) : (413, []);
    }
}
