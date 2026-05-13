namespace AchieveAi.LmDotnetTools.ProcessLauncher;

/// <summary>
/// Pluggable process-launch seam shared by every CLI-agent provider.
/// </summary>
/// <remarks>
/// The default implementation (<see cref="DefaultProcessLauncher"/>) spawns the
/// CLI directly via <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>.
/// Custom implementations can route the launch through Docker, Kubernetes, SSH,
/// a sandboxed VM, or any other host without provider involvement — the
/// provider only ever talks to the returned <see cref="IProcessHandle"/>.
/// </remarks>
public interface IProcessLauncher
{
    /// <summary>
    /// Launch the process described by <paramref name="request"/>. Returns a
    /// handle the caller uses for stdio, liveness probes, and termination.
    /// Implementations should throw <see cref="ProcessLauncherException"/>
    /// (wrapping the underlying cause) when the launch fails.
    /// </summary>
    IProcessHandle Launch(ProcessLaunchRequest request, CancellationToken cancellationToken = default);
}
