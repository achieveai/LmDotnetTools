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
        var (status, workspace) = await ResolveWorkspaceAsync(threadId, principal, ct);
        if (status != 200 || workspace is null)
            return new(status, null, null);
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

    /// <summary>
    /// Authorizes an interactive end user to write to <paramref name="threadId"/> and returns the conversation's
    /// workspace id, without touching any sandbox session.
    /// </summary>
    public async Task<(int Status, string? WorkspaceId)> ResolveWorkspaceAsync(
        string threadId,
        Principal? principal,
        CancellationToken ct
    )
    {
        if (
            (principal is null && authorizer.IsEnforced)
            || (
                principal is not null
                && (principal.Source != PrincipalSource.Interactive || principal.Actor.Kind != PrincipalKind.EndUser)
            )
        )
            return (401, null);

        var metadata = await conversations.LoadMetadataAsync(threadId, ct);
        var decision = await authorizer.AuthorizeAsync(principal, threadId, metadata, AccessAction.Write, ct);
        if (!decision.Allowed)
            return (decision.HidesExistence ? 404 : 403, null);
        if (metadata is null)
            return (404, null);
        if (
            metadata.Properties is null
            || !metadata.Properties.TryGetValue(MultiTurnAgentPool.WorkspacePropertyKey, out var value)
        )
            return (409, null);
        var workspace = value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(workspace) ? (409, null) : (200, workspace);
    }
}
