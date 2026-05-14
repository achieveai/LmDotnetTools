namespace AchieveAi.LmDotnetTools.ProcessLauncher;

/// <summary>
/// Abstraction over a launched process. Models the surface providers actually
/// touch on <see cref="System.Diagnostics.Process"/> today (liveness probe,
/// exit handling, stdio streams, termination) without exposing the OS process
/// type — so a Docker / SSH / remote launcher can return a handle that has
/// no in-proc <see cref="System.Diagnostics.Process"/>.
/// </summary>
public interface IProcessHandle : IAsyncDisposable, IDisposable
{
    /// <summary>True once the process has exited. Synchronous snapshot.</summary>
    bool HasExited { get; }

    /// <summary>True while the process is still running. Mirrors <c>!HasExited</c>
    /// and is provided for call-site clarity.</summary>
    bool IsRunning => !HasExited;

    /// <summary>Exit code, or <c>null</c> if the process has not exited yet
    /// or the launcher cannot observe it (e.g., remote launcher with no
    /// reliable status channel).</summary>
    int? ExitCode { get; }

    /// <summary>OS process id. Returns <c>null</c> for non-OS launchers
    /// (containers, remote shells, in-process simulators).</summary>
    int? ProcessId { get; }

    /// <summary>Resolves once the process has exited, returning the exit code.
    /// Implementations that cannot observe an exit code should resolve with
    /// <c>0</c>.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>Synchronously waits up to <paramref name="timeout"/> for the
    /// process to exit. Returns <c>true</c> if the process exited within the
    /// timeout, <c>false</c> otherwise. Used by sync <c>Dispose</c> paths that
    /// cannot await — avoids busy-wait polling on the calling thread.</summary>
    bool WaitForExit(TimeSpan timeout);

    /// <summary>Raised once when <see cref="HasExited"/> first transitions to
    /// true. Subscribers MUST tolerate being invoked from arbitrary threads.</summary>
    event EventHandler? Exited;

    /// <summary>Stdin writer. Maps 1:1 to
    /// <see cref="System.Diagnostics.Process.StandardInput"/>. The
    /// underlying byte stream is reachable via <see cref="StreamWriter.BaseStream"/>
    /// for callers that need a custom encoding wrapper.</summary>
    StreamWriter StandardInput { get; }

    /// <summary>Stdout reader. Maps 1:1 to
    /// <see cref="System.Diagnostics.Process.StandardOutput"/>.</summary>
    StreamReader StandardOutput { get; }

    /// <summary>Stderr reader. Maps 1:1 to
    /// <see cref="System.Diagnostics.Process.StandardError"/>.</summary>
    StreamReader StandardError { get; }

    /// <summary>Force-terminate the process. <paramref name="entireProcessTree"/>
    /// requests that descendant processes also be killed; launchers that cannot
    /// observe descendants ignore the flag.</summary>
    void Kill(bool entireProcessTree = true);
}
