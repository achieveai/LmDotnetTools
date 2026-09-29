using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.SandboxApps;

/// <summary>Proves this live workspace session supports the streaming operation protocol.</summary>
public sealed class SandboxAppCapability(IWorkspaceFileBrowser browser)
{
    public async Task<bool> IsReadyAsync(string sessionId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (!await browser.SupportsStreamingAsync(sessionId, timeout.Token))
                return false;
            return true;
        }
        catch (Exception ex) when (ex is SandboxException or NotSupportedException or OperationCanceledException)
        {
            return false;
        }
    }
}
