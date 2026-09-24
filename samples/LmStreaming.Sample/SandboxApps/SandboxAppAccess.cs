using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.SandboxApps;

public sealed record SandboxAppContext(int Status, string? SessionId, string? WorkspaceId);

/// <summary>One request's authorization and non-creating workspace session lookup.</summary>
public sealed class SandboxAppAccess(
    IConversationStore conversations,
    IWorkspaceFileBrowser browser,
    Identity.ConversationAuthorizer authorizer
)
{
    public async Task<(int Status, string? SessionId)> ResolveAsync(
        string threadId,
        Principal? principal,
        CancellationToken ct
    )
    {
        var context = await ResolveContextAsync(threadId, principal, ct);
        return (context.Status, context.SessionId);
    }

    public async Task<SandboxAppContext> ResolveContextAsync(
        string threadId,
        Principal? principal,
        CancellationToken ct
    )
    {
        if (
            principal is null
            || principal.Source != PrincipalSource.Interactive
            || principal.Actor.Kind != PrincipalKind.EndUser
        )
            return new(401, null, null);

        var metadata = await conversations.LoadMetadataAsync(threadId, ct);
        var decision = await authorizer.AuthorizeAsync(principal, threadId, metadata, AccessAction.Write, ct);
        if (!decision.Allowed)
            return new(decision.HidesExistence ? 404 : 403, null, null);
        if (metadata is null)
            return new(404, null, null);
        if (
            metadata.Properties is null
            || !metadata.Properties.TryGetValue(MultiTurnAgentPool.WorkspacePropertyKey, out var value)
        )
            return new(409, null, null);
        var workspace = value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(workspace))
            return new(409, null, null);
        SandboxSessionResolution session;
        try
        {
            session = await browser.ResolveThreadWorkspaceSessionAsync(threadId, workspace, null, ct);
        }
        catch (SandboxSessionUnavailableException)
        {
            return new(503, null, null);
        }
        catch (AchieveAi.LmDotnetTools.Sandbox.SandboxException)
        {
            return new(503, null, null);
        }
        return session.Outcome switch
        {
            SandboxSessionResolutionOutcome.Resolved
                when session.Session is not null && session.Session.WorkspaceId == workspace => new(
                200,
                session.Session.SessionId,
                workspace
            ),
            SandboxSessionResolutionOutcome.CredentialConflict => new(403, null, null),
            _ => new(409, null, null),
        };
    }
}
