using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

/// <summary>Invokes contained workspace scripts using a typed stdin envelope and bounded redirected streams.</summary>
public sealed class WorkflowScriptInvoker
{
    private const string BootstrapReady = "__LM_WORKFLOW_SCOPE_READY__";
    private const string BootstrapFailed = "__LM_WORKFLOW_SCOPE_FAILED__";
    private readonly string _pythonExecutable;
    private readonly string _powerShellExecutable;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly int _maximumOutputCharacters;
    private readonly bool _forceContainmentProbeFailure;
    private readonly bool _forceFinalContainmentProbeFailure;
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
        : this(pythonExecutable, powerShellExecutable, environment, maximumOutputCharacters, false) { }

    private WorkflowScriptInvoker(
        string pythonExecutable,
        string powerShellExecutable,
        IReadOnlyDictionary<string, string>? environment,
        int maximumOutputCharacters,
        bool forceContainmentProbeFailure,
        bool forceFinalContainmentProbeFailure = false
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(powerShellExecutable);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputCharacters);
        _pythonExecutable = pythonExecutable;
        _powerShellExecutable = powerShellExecutable;
        _environment = environment ?? new Dictionary<string, string>();
        _maximumOutputCharacters = maximumOutputCharacters;
        _forceContainmentProbeFailure = forceContainmentProbeFailure;
        _forceFinalContainmentProbeFailure = forceFinalContainmentProbeFailure;
    }

    internal WorkflowScriptInvoker(bool forceContainmentProbeFailure, bool forceFinalContainmentProbeFailure = false)
        : this(
            "python",
            "pwsh",
            environment: null,
            8 * 1024 * 1024,
            forceContainmentProbeFailure,
            forceFinalContainmentProbeFailure
        ) { }

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
            throw new InvalidOperationException("Could not start the workflow containment bootstrap.");
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? termination = null;
        using var registration = stop.Token.Register(() => termination = TerminateScopeAsync(process));
        var stderr = ReadBoundedAsync(process.StandardError, "stderr", stop);
        Task<string>? stdout = null;
        Task? write = null;
        Task? exit = null;
        try
        {
            await ReadBootstrapReadyAsync(process.StandardOutput, stop.Token).ConfigureAwait(false);
            stdout = ReadBoundedAsync(process.StandardOutput, "stdout", stop);
            write = WriteInputAsync(process, envelope, stop.Token);
            exit = process.WaitForExitAsync(stop.Token);
            await Task.WhenAll(stdout, stderr, write, exit).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var output = await stdout.ConfigureAwait(false);
            var diagnostic = await stderr.ConfigureAwait(false);
            if (ContainsContainmentFailure(output) || ContainsContainmentFailure(diagnostic))
            {
                throw new WorkflowScriptTerminationException(
                    "The workflow containment scope could not be settled; the workspace cannot be reused."
                );
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Workflow script exited with code {process.ExitCode}: {diagnostic}"
                );
            }
            return output;
        }
        catch (WorkflowScriptTerminationException)
        {
            try
            {
                await (termination ?? TerminateScopeAsync(process)).ConfigureAwait(false);
            }
            catch (WorkflowScriptTerminationException)
            {
                // The original containment failure is the more useful diagnosis.
            }
            throw;
        }
        catch
        {
            var exitWasObserved = exit?.IsCompletedSuccessfully == true || process.HasExited;
            await (termination ?? TerminateScopeAsync(process)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (
                ContainsContainmentFailure(await GetCompletedTextAsync(stdout).ConfigureAwait(false))
                || ContainsContainmentFailure(await GetCompletedTextAsync(stderr).ConfigureAwait(false))
            )
            {
                throw new WorkflowScriptTerminationException(
                    "The workflow containment scope could not be settled; the workspace cannot be reused."
                );
            }
            if (stdout?.Exception?.GetBaseException() is InvalidOperationException outputError)
            {
                throw outputError;
            }
            if (stderr.Exception?.GetBaseException() is InvalidOperationException diagnosticError)
            {
                throw diagnosticError;
            }
            if (exitWasObserved && process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Workflow script exited with code {process.ExitCode}: {await GetCompletedTextAsync(stderr).ConfigureAwait(false)}"
                );
            }
            throw;
        }
    }

    private static async Task<string> GetCompletedTextAsync(Task<string>? task)
    {
        if (task is not { IsCompletedSuccessfully: true })
        {
            return string.Empty;
        }
        return await task.ConfigureAwait(false);
    }

    private static bool ContainsContainmentFailure(string text) =>
        text.Contains(BootstrapFailed, StringComparison.Ordinal);

    private static async Task ReadBootstrapReadyAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line == BootstrapReady)
        {
            return;
        }
        if (line?.StartsWith(BootstrapFailed, StringComparison.Ordinal) == true)
        {
            throw new WorkflowScriptTerminationException(
                $"The workflow containment scope could not be established; the workspace cannot be reused ({line})."
            );
        }
        throw new WorkflowScriptTerminationException(
            "The workflow containment bootstrap did not confirm ownership before running the script."
        );
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

    private static async Task TerminateScopeAsync(Process process)
    {
        var scopeSignalSent = !OperatingSystem.IsLinux() || TryKillProcessGroup(process.Id);
        TryKill(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new WorkflowScriptTerminationException(
                "Script scope termination could not be confirmed; the workspace cannot be reused."
            );
        }
        if (!scopeSignalSent)
        {
            throw new WorkflowScriptTerminationException(
                "Script scope termination could not be confirmed; the workspace cannot be reused."
            );
        }
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
            // The bootstrap exited between the probe and kill. Its job/process group remains the scope owner.
        }
        catch (Win32Exception)
        {
            // The bounded wait above distinguishes a failed kill from a process that has already exited.
        }
    }

    private static bool TryKillProcessGroup(int processGroup)
    {
        const int sigKill = 9;
        if (kill(-processGroup, sigKill) == 0)
        {
            return true;
        }
        return Marshal.GetLastPInvokeError() == 3; // ESRCH: the group has already ended.
    }

    private ProcessStartInfo CreateStartInfo(string script, string workspace)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Workflow script containment requires Windows or Linux and refuses to start on this operating system."
            );
        }

        var extension = Path.GetExtension(script);
        var python = extension.Equals(".py", StringComparison.OrdinalIgnoreCase);
        if (!python && !extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only .py and .ps1 workflow scripts are supported.", nameof(script));
        }
        var start = new ProcessStartInfo
        {
            FileName = _pythonExecutable,
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
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(ContainmentBootstrap);
        start.ArgumentList.Add(python ? _pythonExecutable : _powerShellExecutable);
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
        if (_forceContainmentProbeFailure)
        {
            start.Environment["LM_WORKFLOW_FORCE_SCOPE_PROBE_FAILURE"] = "1";
        }
        if (_forceFinalContainmentProbeFailure)
        {
            start.Environment["LM_WORKFLOW_FORCE_FINAL_SCOPE_PROBE_FAILURE"] = "1";
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

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    // This trusted bootstrap owns the scope before it starts workflow-controlled code. Windows places itself in a
    // kill-on-close Job Object, so its descendants inherit the job at creation. Linux becomes a session/process-group
    // leader before spawning the interpreter. It intentionally does not defend against a trusted script that invokes
    // platform-specific breakaway/detach APIs; workflow scripts are foreground-contract code.
    private const string ContainmentBootstrap = """
import ctypes
import ctypes.wintypes
import os
import subprocess
import sys

READY = "__LM_WORKFLOW_SCOPE_READY__"
FAILED = "__LM_WORKFLOW_SCOPE_FAILED__"

def signal(value):
    print(value, flush=True)

def fail(value):
    signal(FAILED + ":" + value)
    raise SystemExit(252)

def linux_scope():
    try:
        os.setsid()
    except OSError as error:
        fail("setsid-" + str(error.errno))

def linux_has_residual(scope):
    if os.environ.get("LM_WORKFLOW_FORCE_FINAL_SCOPE_PROBE_FAILURE") == "1":
        fail("probe-injected")
    try:
        for name in os.listdir("/proc"):
            if not name.isdigit() or int(name) == os.getpid():
                continue
            try:
                with open("/proc/" + name + "/stat", encoding="utf-8") as stat:
                    tail = stat.read().rsplit(")", 1)[1].split()
                if len(tail) > 2 and int(tail[2]) == scope:
                    return True
            except FileNotFoundError:
                continue
            except ProcessLookupError:
                continue
    except (OSError, ValueError, IndexError):
        fail("probe-unreadable")
    return False

def windows_scope():
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p]
    kernel32.CreateJobObjectW.restype = ctypes.c_void_p
    kernel32.SetInformationJobObject.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.wintypes.DWORD]
    kernel32.SetInformationJobObject.restype = ctypes.wintypes.BOOL
    kernel32.AssignProcessToJobObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    kernel32.AssignProcessToJobObject.restype = ctypes.wintypes.BOOL
    kernel32.GetCurrentProcess.restype = ctypes.c_void_p
    kernel32.QueryInformationJobObject.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.wintypes.DWORD, ctypes.c_void_p]
    kernel32.QueryInformationJobObject.restype = ctypes.wintypes.BOOL
    kernel32.TerminateJobObject.argtypes = [ctypes.c_void_p, ctypes.wintypes.UINT]
    kernel32.TerminateJobObject.restype = ctypes.wintypes.BOOL
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    kernel32.CloseHandle.restype = ctypes.wintypes.BOOL
    class BasicLimit(ctypes.Structure):
        _fields_ = [("PerProcessUserTimeLimit", ctypes.c_longlong), ("PerJobUserTimeLimit", ctypes.c_longlong),
                   ("LimitFlags", ctypes.wintypes.DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t),
                   ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", ctypes.wintypes.DWORD),
                   ("Affinity", ctypes.c_size_t), ("PriorityClass", ctypes.wintypes.DWORD),
                   ("SchedulingClass", ctypes.wintypes.DWORD)]
    class IoCounters(ctypes.Structure):
        _fields_ = [("ReadOperationCount", ctypes.c_ulonglong), ("WriteOperationCount", ctypes.c_ulonglong),
                   ("OtherOperationCount", ctypes.c_ulonglong), ("ReadTransferCount", ctypes.c_ulonglong),
                   ("WriteTransferCount", ctypes.c_ulonglong), ("OtherTransferCount", ctypes.c_ulonglong)]
    class ExtendedLimit(ctypes.Structure):
        _fields_ = [("BasicLimitInformation", BasicLimit), ("IoInfo", IoCounters),
                   ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                   ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]
    class Accounting(ctypes.Structure):
        _fields_ = [("TotalUserTime", ctypes.c_longlong), ("TotalKernelTime", ctypes.c_longlong),
                   ("ThisPeriodTotalUserTime", ctypes.c_longlong), ("ThisPeriodTotalKernelTime", ctypes.c_longlong),
                   ("TotalPageFaultCount", ctypes.wintypes.DWORD), ("TotalProcesses", ctypes.wintypes.DWORD),
                   ("ActiveProcesses", ctypes.wintypes.DWORD), ("TotalTerminatedProcesses", ctypes.wintypes.DWORD)]
    job = kernel32.CreateJobObjectW(None, None)
    if not job:
        fail("job-create-" + str(ctypes.get_last_error()))
    limits = ExtendedLimit()
    limits.BasicLimitInformation.LimitFlags = 0x00002000
    if not kernel32.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
        fail("job-configure-" + str(ctypes.get_last_error()))
    if not kernel32.AssignProcessToJobObject(job, kernel32.GetCurrentProcess()):
        fail("job-assign-" + str(ctypes.get_last_error()))
    def residual():
        if os.environ.get("LM_WORKFLOW_FORCE_FINAL_SCOPE_PROBE_FAILURE") == "1":
            fail("probe-injected")
        accounting = Accounting()
        if not kernel32.QueryInformationJobObject(job, 1, ctypes.byref(accounting), ctypes.sizeof(accounting), None):
            fail("job-query-" + str(ctypes.get_last_error()))
        return accounting.ActiveProcesses > 1
    def clean():
        limits = ExtendedLimit()
        if not kernel32.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
            fail("job-release-" + str(ctypes.get_last_error()))
        kernel32.CloseHandle(job)
    def terminate():
        signal(FAILED + ":residual")
        sys.stdout.flush()
        kernel32.TerminateJobObject(job, 1)
        os._exit(252)
    return residual, clean, terminate

def main():
    if len(sys.argv) < 2:
        fail("missing-target")
    if os.environ.get("LM_WORKFLOW_FORCE_SCOPE_PROBE_FAILURE") == "1":
        fail("probe-injected")
    if os.name == "nt":
        residual, clean, terminate = windows_scope()
    elif sys.platform.startswith("linux"):
        linux_scope()
        scope = os.getpgrp()
        residual = lambda: linux_has_residual(scope)
        clean = lambda: None
        def terminate():
            signal(FAILED + ":residual")
            sys.stdout.flush()
            os.killpg(scope, 9)
            os._exit(252)
    else:
        fail("unsupported-platform")
    signal(READY)
    child = subprocess.Popen(sys.argv[1:], stdin=sys.stdin.buffer, stdout=sys.stdout.buffer, stderr=sys.stderr.buffer)
    status = child.wait()
    if residual():
        terminate()
    clean()
    raise SystemExit(status)

try:
    main()
except SystemExit:
    raise
except BaseException as error:
    fail("bootstrap-" + type(error).__name__)
""";
}

/// <summary>Signals that child exit was not established and the host must preserve the unavailable workspace.</summary>
public sealed class WorkflowScriptTerminationException(string message) : InvalidOperationException(message);
