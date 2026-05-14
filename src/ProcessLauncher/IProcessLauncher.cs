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
///
/// <see cref="LaunchAsync"/> is the primary contract; remote / containerised
/// launchers can perform async I/O (image pull, pod scheduling, SSH handshake)
/// without blocking the caller. <see cref="Launch"/> remains as a default
/// interface method for simple in-proc impls and sync call sites.
/// </remarks>
public interface IProcessLauncher
{
    /// <summary>
    /// Launch the process described by <paramref name="request"/>. Returns a
    /// handle the caller uses for stdio, liveness probes, and termination.
    /// Implementations should throw <see cref="ProcessLauncherException"/>
    /// (wrapping the underlying cause) when the launch fails.
    /// </summary>
    Task<IProcessHandle> LaunchAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous convenience wrapper around <see cref="LaunchAsync"/>. The
    /// default implementation blocks on the async call; sync-native launchers
    /// may override this to avoid the extra Task allocation.
    /// </summary>
    /// <remarks>
    /// ⚠️ Implementations whose <see cref="LaunchAsync"/> performs real async
    /// I/O (image pull, pod scheduling, SSH handshake) MUST override this
    /// method with a sync-native or sync-over-async-without-context impl.
    /// The default body uses <c>GetAwaiter().GetResult()</c>, which can
    /// deadlock when called from a captured <see cref="SynchronizationContext"/>
    /// (UI / classic ASP.NET) and starves thread-pool threads under load.
    /// </remarks>
    IProcessHandle Launch(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
        => LaunchAsync(request, cancellationToken).GetAwaiter().GetResult();
}
