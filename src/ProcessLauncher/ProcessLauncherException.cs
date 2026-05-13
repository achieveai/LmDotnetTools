namespace AchieveAi.LmDotnetTools.ProcessLauncher;

/// <summary>
/// Thrown by an <see cref="IProcessLauncher"/> when the spawn fails. The
/// inner exception (when present) carries the underlying I/O cause.
/// </summary>
public sealed class ProcessLauncherException : Exception
{
    public ProcessLauncherException(string message) : base(message) { }

    public ProcessLauncherException(string message, Exception innerException)
        : base(message, innerException) { }
}
