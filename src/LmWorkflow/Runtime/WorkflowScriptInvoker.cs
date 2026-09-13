using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

/// <summary>Invokes contained workspace scripts using a typed stdin envelope and bounded redirected streams.</summary>
public sealed class WorkflowScriptInvoker
{
    private readonly string _pythonExecutable;
    private readonly string _powerShellExecutable;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly int _maximumOutputCharacters;
    private Func<Process, List<Process>> _captureDescendants = CaptureDescendants;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
    );
    private readonly ConcurrentDictionary<string, byte> _unavailableWorkspaces = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
    );

    /// <summary>Interpreter names and environment are trusted host configuration, never workflow input.</summary>
    public WorkflowScriptInvoker(
        string pythonExecutable = "python",
        string powerShellExecutable = "pwsh",
        IReadOnlyDictionary<string, string>? environment = null,
        int maximumOutputCharacters = 8 * 1024 * 1024
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(powerShellExecutable);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputCharacters);
        _pythonExecutable = pythonExecutable;
        _powerShellExecutable = powerShellExecutable;
        _environment = environment ?? new Dictionary<string, string>();
        _maximumOutputCharacters = maximumOutputCharacters;
    }

    internal WorkflowScriptInvoker(Func<Process, List<Process>> captureDescendants)
        : this()
    {
        _captureDescendants = captureDescendants ?? throw new ArgumentNullException(nameof(captureDescendants));
    }

    /// <summary>
    /// Returns complete stdout on exit zero. One host-owned instance serializes script operations in each workspace;
    /// its semaphore is released before any subsequent agent invocation. Failed/cancelled children are killed and reaped.
    /// Scripts must wait for all child work; deliberately detached background processes are outside this trusted foreground contract.
    /// </summary>
    public async Task<string> InvokeAsync(
        string scriptPath,
        string workspaceDirectory,
        JsonObject context,
        JsonNode input,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceDirectory));
        var script = ResolveWorkspaceAsset(scriptPath, workspace);
        var start = CreateStartInfo(script, workspace);
        var envelope = new JsonObject
        {
            ["Context"] = context.DeepClone(),
            ["Input"] = input.DeepClone(),
        }.ToJsonString();
        var gate = _workspaceGates.GetOrAdd(workspace, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_unavailableWorkspaces.ContainsKey(workspace))
            {
                throw new WorkflowScriptTerminationException(
                    "Workspace remains unavailable after an unconfirmed script termination."
                );
            }
            return await RunAsync(start, envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowScriptTerminationException)
        {
            _ = _unavailableWorkspaces.TryAdd(workspace, 0);
            throw;
        }
        finally
        {
            _ = gate.Release();
        }
    }

    private async Task<string> RunAsync(ProcessStartInfo start, string envelope, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start the workflow interpreter.");
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Killing at cancellation also closes inherited pipes; merely cancelling WaitForExitAsync leaves the process running.
        Task? termination = null;
        using var registration = stop.Token.Register(() => termination = TerminateTreeAsync(process));
        using var descendants = new DescendantTracker(process, _captureDescendants);
        var stdout = ReadBoundedAsync(process.StandardOutput, "stdout", stop);
        var stderr = ReadBoundedAsync(process.StandardError, "stderr", stop);
        var write = WriteInputAsync(process, envelope, stop.Token);
        var observe = descendants.ObserveUntilExitAsync(stop);
        try
        {
            await Task.WhenAll(stdout, stderr, write, process.WaitForExitAsync(stop.Token)).ConfigureAwait(false);
            await observe.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Workflow script exited with code {process.ExitCode}: {await stderr.ConfigureAwait(false)}"
                );
            }
            await descendants.EnsureSettledAsync().ConfigureAwait(false);
            return await stdout.ConfigureAwait(false);
        }
        catch
        {
            await (termination ?? TerminateTreeAsync(process)).ConfigureAwait(false);
            if (descendants.Failure is { } treeError)
            {
                throw treeError;
            }
            cancellationToken.ThrowIfCancellationRequested();
            // An output-limit failure cancels its sibling reads. Preserve its useful diagnosis instead of a sibling cancellation.
            if (stdout.Exception?.GetBaseException() is InvalidOperationException outputError)
            {
                throw outputError;
            }
            if (stderr.Exception?.GetBaseException() is InvalidOperationException diagnosticError)
            {
                throw diagnosticError;
            }
            throw;
        }
    }

    private static async Task WriteInputAsync(Process process, string envelope, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteAsync(envelope.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
    }

    private async Task<string> ReadBoundedAsync(StreamReader reader, string stream, CancellationTokenSource stop)
    {
        var buffer = new char[4096];
        var result = new StringBuilder();
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), stop.Token).ConfigureAwait(false)) > 0)
            {
                if (result.Length + count > _maximumOutputCharacters)
                {
                    throw new InvalidOperationException(
                        $"Workflow script {stream} exceeded the configured output limit."
                    );
                }
                _ = result.Append(buffer, 0, count);
            }
            return result.ToString();
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo(string script, string workspace)
    {
        var extension = Path.GetExtension(script);
        var python = extension.Equals(".py", StringComparison.OrdinalIgnoreCase);
        if (!python && !extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only .py and .ps1 workflow scripts are supported.", nameof(script));
        }
        var start = new ProcessStartInfo
        {
            FileName = python ? _pythonExecutable : _powerShellExecutable,
            WorkingDirectory = workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (
            var argument in python
                ? new[] { "-u", script }
                : ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script]
        )
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var (name, value) in _environment)
        {
            start.Environment[name] = value;
        }
        return start;
    }

    /// <summary>Resolves a trusted relative script/skill asset without crossing symbolic links or parent directories.</summary>
    public static string ResolveWorkspaceAsset(string scriptPath, string workspaceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        var workspace = Path.GetFullPath(workspaceDirectory);
        if (
            Path.IsPathRooted(scriptPath)
            || scriptPath.Contains('\\')
            || scriptPath.Contains(':')
            || scriptPath.Split('/').Any(segment => segment is ".." or "." or "")
        )
        {
            throw new ArgumentException("Script path must be a contained workspace-relative path.", nameof(scriptPath));
        }
        var path = workspace;
        foreach (var segment in scriptPath.Split('/'))
        {
            path = Path.Combine(path, segment);
            if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    "Workflow script paths cannot traverse symbolic links.",
                    nameof(scriptPath)
                );
            }
        }
        return path;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the probe and kill. WaitForExitAsync below still reaps it.
        }
        catch (Win32Exception)
        {
            // Permission/OS failure is not termination. The bounded exit wait below must still establish exit.
        }
    }

    private static async Task TerminateTreeAsync(Process process)
    {
        // Capture handles before killing the parent: after reparenting, a living grandchild is no longer discoverable by parent ID.
        List<Process> descendants;
        try
        {
            descendants = CaptureDescendants(process);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            TryKill(process);
            throw new WorkflowScriptTerminationException(
                "Could not establish the script process tree; the workspace cannot be reused."
            );
        }
        try
        {
            foreach (var child in descendants.AsEnumerable().Reverse())
            {
                TryKill(child);
            }
            TryKill(process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await Task.WhenAll(descendants.Append(process).Select(child => child.WaitForExitAsync(timeout.Token)))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new WorkflowScriptTerminationException(
                    "Script tree termination could not be confirmed; the workspace cannot be reused."
                );
            }
        }
        finally
        {
            foreach (var child in descendants)
            {
                child.Dispose();
            }
        }
    }

    private static List<Process> CaptureDescendants(Process root)
    {
        if (root.HasExited)
        {
            return [];
        }
        var parents = OperatingSystem.IsWindows() ? WindowsParents() : LinuxParents();
        var descendants = new List<Process>();
        var ids = new HashSet<int> { root.Id };
        bool added;
        do
        {
            added = false;
            foreach (var (pid, parent) in parents)
            {
                if (!ids.Contains(parent) || !ids.Add(pid))
                {
                    continue;
                }
                added = true;
                try
                {
                    var child = Process.GetProcessById(pid);
                    _ = child.Handle;
                    descendants.Add(child);
                }
                catch (ArgumentException) { }
            }
        } while (added);
        return descendants;
    }

    private static Dictionary<int, int> LinuxParents()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new WorkflowScriptTerminationException(
                "Process-tree settlement requires a supported operating system."
            );
        }
        var parents = new Dictionary<int, int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid))
            {
                continue;
            }
            try
            {
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                parents[pid] = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return parents;
    }

    /// <summary>
    /// Keeps handles to every descendant observed while the script parent is alive. A successful parent cannot release
    /// the workspace while one of those handles is still live: the child is terminated and the workspace is quarantined.
    /// </summary>
    private sealed class DescendantTracker(Process root, Func<Process, List<Process>> capture) : IDisposable
    {
        private readonly Process _root = root;
        private readonly Dictionary<int, Process> _descendants = [];
        public WorkflowScriptTerminationException? Failure { get; private set; }

        public async Task ObserveUntilExitAsync(CancellationTokenSource stop)
        {
            try
            {
                while (!_root.HasExited)
                {
                    Observe();
                    await Task.Delay(TimeSpan.FromMilliseconds(5)).ConfigureAwait(false);
                }
            }
            catch (WorkflowScriptTerminationException exception)
            {
                Failure = exception;
                if (!stop.IsCancellationRequested)
                {
                    await stop.CancelAsync().ConfigureAwait(false);
                }
                throw;
            }
        }

        public async Task EnsureSettledAsync()
        {
            var live = _descendants.Values.Where(process => !process.HasExited).ToArray();
            if (live.Length == 0)
            {
                return;
            }

            using var gracePeriod = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await Task.WhenAll(live.Select(process => process.WaitForExitAsync(gracePeriod.Token)))
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { }

            await TerminateAsync().ConfigureAwait(false);
            throw new WorkflowScriptTerminationException(
                "A successful workflow script left child processes running; the workspace cannot be reused."
            );
        }

        public async Task TerminateAsync()
        {
            foreach (var process in _descendants.Values.Reverse())
            {
                TryKill(process);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await Task.WhenAll(_descendants.Values.Select(process => process.WaitForExitAsync(timeout.Token)))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new WorkflowScriptTerminationException(
                    "Script child termination could not be confirmed; the workspace cannot be reused."
                );
            }
        }

        private void Observe()
        {
            List<Process> observed;
            try
            {
                observed = capture(_root);
            }
            catch (WorkflowScriptTerminationException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new WorkflowScriptTerminationException(
                    "Could not establish the script process tree; the workspace cannot be reused."
                );
            }
            foreach (var process in observed)
            {
                if (!_descendants.TryAdd(process.Id, process))
                {
                    process.Dispose();
                }
            }
        }

        public void Dispose()
        {
            foreach (var process in _descendants.Values)
            {
                process.Dispose();
            }
        }
    }

    private static Dictionary<int, int> WindowsParents()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid)
        {
            throw new WorkflowScriptTerminationException("Cannot enumerate the script process tree.");
        }
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var parents = new Dictionary<int, int>();
        var present = Process32First(snapshot, ref entry);
        while (present)
        {
            parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            present = Process32Next(snapshot, ref entry);
        }
        if (Marshal.GetLastWin32Error() != 18)
        {
            throw new WorkflowScriptTerminationException("Cannot fully enumerate the script process tree.");
        }
        return parents;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr Heap;
        public uint Module;
        public uint Threads;
        public uint ParentProcessId;
        public int Priority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Executable;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}

/// <summary>Signals that child exit was not established and the host must preserve the unavailable workspace.</summary>
public sealed class WorkflowScriptTerminationException(string message) : InvalidOperationException(message);
