using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Persistence;

namespace LmStreaming.Sample.SandboxApps;

public interface ISandboxAppModeReadiness
{
    Task<bool> IsReadyAsync(string workspaceId, CancellationToken ct);
    Task<bool> ActivateAsync(string workspaceId, CancellationToken ct);
}

/// <summary>Checks the selected workspace's live gateway before offering the builder mode.</summary>
public sealed class SandboxAppModeReadiness(
    IWorkspaceStore workspaces,
    SandboxSessionRegistry sessions,
    SandboxAppCapability capability
) : ISandboxAppModeReadiness
{
    public async Task<bool> IsReadyAsync(string workspaceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var workspace = await workspaces.GetAsync(workspaceId, timeout.Token);
            if (workspace is null)
                return false;
            var session = sessions.TryGetExistingSession(workspaceId);
            if (session is null)
                return false;
            return await capability.IsReadyAsync(session.SessionId, timeout.Token);
        }
        catch (Exception ex)
            when (ex
                    is SandboxException
                        or SandboxSessionUnavailableException
                        or HttpRequestException
                        or OperationCanceledException
            )
        {
            return false;
        }
    }

    public async Task<bool> ActivateAsync(string workspaceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var workspace = await workspaces.GetAsync(workspaceId, timeout.Token);
            if (workspace is null)
                return false;
            var session = await sessions.GetOrCreateLiveSessionAsync(
                global::Program.BuildWorkspaceRef(workspaceId, workspace),
                timeout.Token
            );
            return await capability.IsReadyAsync(session.SessionId, timeout.Token);
        }
        catch (Exception ex)
            when (ex
                    is SandboxException
                        or SandboxSessionUnavailableException
                        or HttpRequestException
                        or OperationCanceledException
            )
        {
            return false;
        }
    }
}
