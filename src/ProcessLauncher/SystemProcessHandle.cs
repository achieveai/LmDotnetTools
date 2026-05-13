using System.Diagnostics;

namespace AchieveAi.LmDotnetTools.ProcessLauncher;

/// <summary>
/// Default <see cref="IProcessHandle"/> implementation that wraps a real
/// <see cref="Process"/>. Forwards every member to the underlying process and
/// raises <see cref="Exited"/> via <see cref="Process.Exited"/>.
/// </summary>
internal sealed class SystemProcessHandle : IProcessHandle
{
    private readonly Process _process;
    private int _disposed;
    private int _exitedRaised;

    public SystemProcessHandle(Process process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));

        // Hook Process.Exited so callers can observe termination without
        // polling. Process.EnableRaisingEvents must be true for this event
        // to fire.
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;

        // Guard for the (rare but possible) race where the process exited
        // between Start and event wire-up.
        if (_process.HasExited)
        {
            OnProcessExited(this, EventArgs.Empty);
        }
    }

    public bool HasExited => _process.HasExited;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public int? ProcessId
    {
        get
        {
            try
            {
                return _process.Id;
            }
            catch (InvalidOperationException)
            {
                // Process has been disposed or never started.
                return null;
            }
        }
    }

    public event EventHandler? Exited;

    public StreamWriter StandardInput => _process.StandardInput;

    public StreamReader StandardOutput => _process.StandardOutput;

    public StreamReader StandardError => _process.StandardError;

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        return WaitForExitCoreAsync(cancellationToken);
    }

    private async Task<int> WaitForExitCoreAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public void Kill(bool entireProcessTree = true)
    {
        if (_process.HasExited)
        {
            return;
        }

        _process.Kill(entireProcessTree);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _process.Exited -= OnProcessExited;
        }
        catch
        {
            // Process may already be disposed; safe to ignore.
        }

        _process.Dispose();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _exitedRaised, 1) != 0)
        {
            return;
        }

        Exited?.Invoke(this, EventArgs.Empty);
    }
}
