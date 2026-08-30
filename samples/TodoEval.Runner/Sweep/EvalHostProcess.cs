using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TodoEval.Runner.Sweep;

/// <summary>
/// The ISOLATED LmStreaming.Sample instance a sweep runs against. The host roots ALL of its on-disk
/// state (conversations, chat-modes, workspaces, run ledger, ...) at <c>AppContext.BaseDirectory</c>
/// — the directory the binary runs from, NOT the content root — so isolation means giving the sweep
/// its own copy of the binaries in a scratch directory and launching from there. This class publishes
/// (or copies pre-published binaries) into that directory, launches the exe as a real child process
/// on its own port, waits for API readiness, and kills the process tree on dispose. It never goes
/// anywhere near a live deployment.
/// </summary>
internal sealed class EvalHostProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly HostConfig _config;

    public string InstanceDir { get; }
    public int Port { get; }
    public Uri BaseAddress { get; }

    /// <summary>The conversation store the metrics extractor reads after the sweep.</summary>
    public string ConversationsDir => Path.Combine(InstanceDir, "conversations");

    private EvalHostProcess(Process process, HostConfig config, string instanceDir, int port)
    {
        _process = process;
        _config = config;
        InstanceDir = instanceDir;
        Port = port;
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
    }

    public static async Task<EvalHostProcess> StartAsync(
        HostConfig config,
        string repoRoot,
        string instanceDir,
        string logDir,
        TextWriter log,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(instanceDir);
        Directory.CreateDirectory(logDir);

        if (config.PublishDir is { } publishDir)
        {
            log.WriteLine($"[host] copying pre-published binaries from {publishDir} ...");
            CopyDirectory(Path.GetFullPath(publishDir), instanceDir);
        }
        else
        {
            var projectPath = Path.IsPathRooted(config.ProjectPath)
                ? config.ProjectPath
                : Path.Combine(repoRoot, config.ProjectPath);
            log.WriteLine($"[host] publishing {projectPath} (-p:BuildClientApp=false) ...");
            await PublishAsync(projectPath, config.Configuration, instanceDir, logDir, ct);
        }

        var port = config.Port > 0 ? config.Port : GetFreeTcpPort();
        var process = Launch(config, instanceDir, logDir, port, log);
        var host = new EvalHostProcess(process, config, instanceDir, port);
        try
        {
            await host.WaitForReadyAsync(logDir, ct);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        log.WriteLine($"[host] ready at {host.BaseAddress}");
        return host;
    }

    private static async Task PublishAsync(
        string projectPath,
        string configuration,
        string outputDir,
        string logDir,
        CancellationToken ct
    )
    {
        var publishLogPath = Path.Combine(logDir, "host-publish.log");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("publish");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputDir);
        startInfo.ArgumentList.Add("-p:BuildClientApp=false");
        startInfo.ArgumentList.Add("--nologo");

        using var publish =
            Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start 'dotnet publish'.");
        var output = await publish.StandardOutput.ReadToEndAsync(ct);
        var errors = await publish.StandardError.ReadToEndAsync(ct);
        await publish.WaitForExitAsync(ct);
        await File.WriteAllTextAsync(publishLogPath, output + Environment.NewLine + errors, ct);

        if (publish.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'dotnet publish' of the host failed with exit code {publish.ExitCode}; see {publishLogPath}."
            );
        }
    }

    private static Process Launch(HostConfig config, string instanceDir, string logDir, int port, TextWriter log)
    {
        var hostDll = Path.Combine(instanceDir, "LmStreaming.Sample.dll");
        if (!File.Exists(hostDll))
        {
            throw new FileNotFoundException(
                $"LmStreaming.Sample.dll not found in the instance dir '{instanceDir}'. "
                    + "When host.publishDir is set it must point at a PUBLISHED LmStreaming.Sample output.",
                hostDll
            );
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = instanceDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(hostDll);
        startInfo.ArgumentList.Add($"--urls=http://127.0.0.1:{port}");
        // The webhook base defaults to :5000 in appsettings; a wrong value silently breaks context
        // discovery, so it is pinned to this instance's own port.
        startInfo.ArgumentList.Add($"--Auth:Webhook:PublicBaseUrl=http://127.0.0.1:{port}");
        // No sandbox gateway on the eval host: spawning is non-fatal-but-noisy, and the eval's task
        // is a pure todo-board exercise.
        startInfo.ArgumentList.Add("--SandboxGateway:AutoSpawn=false");
        foreach (var arg in config.ExtraArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = config.Environment;
        startInfo.Environment["DOTNET_ENVIRONMENT"] = config.Environment;
        // The host's own .env discovery walks UP from its binary dir to the nearest sln/.git — from
        // a scratch dir that finds nothing (or worse, someone else's), so the env file is explicit.
        if (config.EnvFile is { } envFile)
        {
            startInfo.Environment["LMSTREAMING_ENV_FILE"] = Path.GetFullPath(envFile);
        }

        startInfo.Environment["VITE_AUTO_RUN"] = "false";
        foreach (var (key, value) in config.ExtraEnv)
        {
            startInfo.Environment[key] = value;
        }

        var process =
            Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the eval host process.");

        // Drain stdout/stderr to files; an un-drained redirected pipe would eventually block the host.
        _ = PumpAsync(process.StandardOutput, Path.Combine(logDir, "host-stdout.log"));
        _ = PumpAsync(process.StandardError, Path.Combine(logDir, "host-stderr.log"));

        log.WriteLine($"[host] started pid {process.Id} on port {port} (instance: {instanceDir})");
        return process;
    }

    private static async Task PumpAsync(StreamReader reader, string path)
    {
        try
        {
            await using var writer = new StreamWriter(path, append: false);
            writer.AutoFlush = true;
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                await writer.WriteLineAsync(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The host exiting mid-read is the normal end of the pump.
        }
    }

    private async Task WaitForReadyAsync(string logDir, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(_config.ReadinessTimeoutSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The eval host exited with code {_process.ExitCode} before becoming ready; "
                        + $"see host-stderr.log in {logDir}."
                );
            }

            try
            {
                using var response = await http.GetAsync("api/providers", ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-request timeout while the host warms up.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        throw new TimeoutException(
            $"The eval host did not answer GET /api/providers within {_config.ReadinessTimeoutSeconds}s; "
                + $"see host-stdout.log / host-stderr.log in {logDir}."
        );
    }

    /// <summary>
    /// Stops the host. Debounced persistence (notably the todo-board metadata writer) flushes during
    /// and shortly after runs, so a configurable grace elapses before the process tree is killed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            var grace = TimeSpan.FromSeconds(Math.Max(0, _config.ShutdownGraceSeconds));
            if (grace > TimeSpan.Zero)
            {
                await Task.Delay(grace);
            }

            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
                // Already exited between the check and the kill.
            }
        }

        _process.Dispose();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// The host's runtime-state directories/files, relative to its binary dir. A pre-published
    /// source may have been RUN before, so these are excluded from the copy — carrying them over
    /// would seed the "fresh" store with someone else's conversations and poison the metrics.
    /// </summary>
    private static readonly string[] StateEntries =
    [
        "conversations",
        "chat-modes",
        "workspaces",
        "workflow-index",
        "logs",
        "oauth-tokens",
        "recordings",
        "notify-waits.db",
    ];

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"host.publishDir does not exist: {sourceDir}");
        }

        static bool IsState(string relativePath)
        {
            var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            // Exact match for the state directories; prefix for the SQLite db so -wal/-shm/-journal
            // sidecars are excluded with it.
            return StateEntries.Any(entry => string.Equals(firstSegment, entry, StringComparison.OrdinalIgnoreCase))
                || firstSegment.StartsWith("notify-waits.db", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, directory);
            if (!IsState(relative))
            {
                Directory.CreateDirectory(Path.Combine(destinationDir, relative));
            }
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (!IsState(relative))
            {
                File.Copy(file, Path.Combine(destinationDir, relative), overwrite: true);
            }
        }
    }
}
