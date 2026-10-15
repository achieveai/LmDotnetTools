using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.SandboxApps;

/// <summary>Rechecks conversation access and the current mode for every browser call and page request.</summary>
public sealed class BrowserDebugAccess(SandboxAppAccess access, IConversationStore conversations, IChatModeStore modes)
{
    public async Task<SandboxAppContext> ResolveAsync(
        string threadId,
        Principal? principal,
        string? toolName,
        CancellationToken ct
    )
    {
        var context = await access.ResolveContextAsync(threadId, principal, ct);
        if (context.Status != 200)
        {
            return context;
        }
        var metadata = await conversations.LoadMetadataAsync(threadId, ct);
        var modeId =
            metadata?.Properties?.TryGetValue(MultiTurnAgentPool.ModePropertyKey, out var value) == true
                ? value?.ToString()
                : null;
        var mode = modeId is null ? null : await modes.GetModeAsync(modeId, ct);
        var caps = mode is null ? null : ModeCapabilities.Resolve(mode);
        if (
            caps?.BrowserDebugTools != true
            || (toolName is not null && caps.BrowserDebugToolAllowList is { } allowed && !allowed.Contains(toolName))
        )
        {
            return new(403, null, null);
        }
        return context;
    }
}
