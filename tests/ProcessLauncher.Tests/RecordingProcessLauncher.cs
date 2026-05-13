namespace AchieveAi.LmDotnetTools.ProcessLauncher.Tests;

/// <summary>
/// Test double that records the <see cref="ProcessLaunchRequest"/> it receives
/// without spawning anything. Use this to assert what arguments / environment
/// overrides / host paths a provider hands to the launcher seam.
/// </summary>
public sealed class RecordingProcessLauncher : IProcessLauncher
{
    public ProcessLaunchRequest? LastRequest { get; private set; }

    public List<ProcessLaunchRequest> Requests { get; } = [];

    public Func<ProcessLaunchRequest, IProcessHandle>? HandleFactory { get; init; }

    public IProcessHandle Launch(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        Requests.Add(request);

        if (HandleFactory != null)
        {
            return HandleFactory(request);
        }

        throw new InvalidOperationException(
            $"{nameof(RecordingProcessLauncher)} captured a launch request but no "
                + $"{nameof(HandleFactory)} was provided. Inspect "
                + $"{nameof(LastRequest)} and avoid driving full provider startup, "
                + "or supply a fake handle.");
    }
}
