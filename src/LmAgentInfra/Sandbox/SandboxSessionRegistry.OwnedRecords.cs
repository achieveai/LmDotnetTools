using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// The dedicated sandboxes each conversation owns, remembered across restarts through <see cref="IOwnedSandboxStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every create of an owned sandbox records it as the owner's current one for its slot, retiring the one it replaces.
/// A restarted host reuses a current record the gateway still has (a dormant sandbox counts: the gateway keeps it
/// until it is deleted or reaped) and whose webhook secret is still on disk, instead of creating another. When its
/// secret is missing, the record is kept and the fresh create marks it retired, so the conversation's delete still
/// reaches that sandbox. When the gateway confirms it gone (404), its record and secret are removed instead. A
/// plugin-selection candidate is the exception: its preparation never adopts a record (that would carry the old
/// plugins), and records the candidate without retiring the one still serving. An owner release that retires keeps
/// the records and marks them retired; a confirmed delete drops the deleted one. So the records name every sandbox of
/// the conversation that still exists, and the conversation's delete reaches all of them.
/// </para>
/// <para>
/// With no store, nothing is recorded and nothing is reused across a restart.
/// </para>
/// </remarks>
public sealed partial class SandboxSessionRegistry
{
    private readonly IOwnedSandboxStore? _ownedSandboxes;

    /// <summary>
    /// The sandboxes <paramref name="threadId"/> records, current and retired: the ones
    /// <see cref="StartDeletingOwnedSessions(string, SandboxCredential?)"/> deletes. Empty on a host with no store.
    /// </summary>
    public Task<IReadOnlyList<OwnedSandboxRecord>> LoadOwnedSandboxesAsync(
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return _ownedSandboxes is null
            ? Task.FromResult<IReadOnlyList<OwnedSandboxRecord>>([])
            : _ownedSandboxes.LoadAsync(threadId, ct);
    }

    /// <summary>
    /// The owner's current sandbox for <paramref name="workspaceRef"/>'s slot and directory, when this host does not
    /// track it (a restart forgot it), its webhook secret is still on disk, and the gateway still has it. Registered
    /// as this registry's session under <paramref name="credential"/>; the caller commits the client reference it took.
    /// <see langword="null"/> when there is nothing to reuse: the caller creates one. A record whose secret is missing
    /// is kept, and that create marks it retired; one the gateway confirms gone (404) is removed here, with its secret.
    /// Never called for a plugin-selection candidate.
    /// </summary>
    /// <exception cref="SandboxSessionUnavailableException">The gateway could not say whether it still has it.</exception>
    private async Task<SandboxSession?> TryAdoptOwnedSessionAsync(
        WorkspaceRef workspaceRef,
        string workspaceRelPath,
        SandboxCredential credential,
        SandboxClient client,
        SandboxMcpSessionContext? mcpContext,
        CancellationToken ct
    )
    {
        if (_ownedSandboxes is null || workspaceRef.OwnerThreadId is not { } owner)
        {
            return null;
        }

        var records = await _ownedSandboxes.LoadAsync(owner, ct).ConfigureAwait(false);
        var record = records.FirstOrDefault(candidate =>
            !candidate.Retired
            && IsSameSlot(candidate, workspaceRef.Id, credential.AppId, workspaceRef.ReadOnlyEgress)
            && string.Equals(candidate.WorkingDir, workspaceRef.WorkingDir, StringComparison.Ordinal)
            && string.Equals(candidate.GatewayWorkspace, workspaceRelPath, StringComparison.Ordinal)
            // A session this host tracks is in use, or is being replaced (a plugin-selection swap): never adopted twice.
            && !_sessionsById.ContainsKey(candidate.SessionId)
        );
        if (record is null)
        {
            return null;
        }

        if (!await _sessionSecretStore.ExistsAsync(record.SessionId, ct).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Not reusing sandbox session {SessionId} of owner {OwnerThreadId}: its webhook secret is gone, so its "
                    + "webhook calls could not authenticate. A fresh sandbox is created and this one is retired",
                record.SessionId,
                owner
            );
            return null;
        }

        SandboxInfo info;
        try
        {
            info = await client.GetAsync(record.SessionId, ct).ConfigureAwait(false);
        }
        catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.NotFound)
        {
            _logger.LogInformation(
                "Sandbox session {SessionId} of owner {OwnerThreadId} is gone from the gateway; creating a fresh one",
                record.SessionId,
                owner
            );
            // Its secret first: once the record is gone, nothing would ever remove it, and it would keep accepting
            // callbacks for a sandbox that no longer exists.
            await _sessionSecretStore.RemoveAsync(record.SessionId, CancellationToken.None).ConfigureAwait(false);
            await _ownedSandboxes
                .UpdateAsync(owner, kept => [.. kept.Where(other => other.SessionId != record.SessionId)], ct)
                .ConfigureAwait(false);
            return null;
        }
        catch (SandboxException ex)
        {
            // Not knowing is not "gone": creating another here would leave the old one asleep beside it.
            throw new SandboxSessionUnavailableException(
                workspaceRef.Id,
                ex.StatusCode,
                $"Could not check whether the sandbox gateway still has sandbox '{record.SessionId}' of conversation "
                    + $"'{owner}' ({ex.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ex.Kind.ToString()}).",
                ex
            );
        }

        var session = new SandboxSession(
            workspaceRef.Id,
            info.SessionId,
            workspaceRelPath,
            StripLongPathPrefix(info.WorkspaceContainerPath ?? string.Empty),
            info.PluginResolution,
            MarketplaceAliases.ResolveEffective(workspaceRef.Marketplaces, _options.Marketplaces),
            info.Image,
            info.ImageDigest,
            info.ImageReport,
            info.WorkspaceWorkingDir,
            workspaceRef.ReadOnlyEgress,
            owner
        );
        _sessionsById[session.SessionId] = session;
        _sessionCredentials[session.SessionId] = credential;
        if (mcpContext is not null)
        {
            _sessionMcpContexts[session.SessionId] = mcpContext;
        }

        _logger.LogInformation(
            "Reusing sandbox session {SessionId} of owner {OwnerThreadId} from before a restart (gateway status "
                + "{Status}); no sandbox is created",
            session.SessionId,
            owner,
            info.Status
        );
        return session;
    }

    /// <summary>
    /// Records <paramref name="session"/> as its owner's current sandbox for its slot. Unless
    /// <paramref name="retireSameSlot"/> is off (a plugin-selection candidate, which the swap retires the other way
    /// round), the one it replaces is marked retired. A no-op for a shared sandbox or on a host with no store.
    /// </summary>
    private async Task RecordOwnedSessionAsync(
        SandboxSession session,
        SandboxCredential credential,
        string? workingDir,
        bool retireSameSlot,
        CancellationToken ct
    )
    {
        if (_ownedSandboxes is null || session.OwnerThreadId is not { } owner)
        {
            return;
        }

        var created = new OwnedSandboxRecord(
            session.SessionId,
            session.WorkspaceId,
            session.WorkspaceRelPath,
            credential.AppId,
            session.ReadOnlyEgress,
            workingDir,
            Retired: false
        );
        await _ownedSandboxes
            .UpdateAsync(
                owner,
                records =>
                    [
                        .. records.Select(record =>
                            retireSameSlot
                            && !record.Retired
                            && IsSameSlot(record, created.WorkspaceId, created.AppId, created.ReadOnlyEgress)
                                ? record with
                                {
                                    Retired = true,
                                }
                                : record
                        ),
                        created,
                    ],
                ct
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Drops a session the gateway no longer has from its owner's records. Best-effort, as the rest of an eviction is:
    /// a record left behind only costs a later DELETE that answers 404. Skipped while the owner's conversation is being
    /// deleted (see <see cref="_ownersBeingDeleted"/>).
    /// </summary>
    private async Task ForgetOwnedRecordAsync(SandboxSession session)
    {
        if (_ownedSandboxes is null || session.OwnerThreadId is not { } owner || _ownersBeingDeleted.ContainsKey(owner))
        {
            return;
        }

        try
        {
            await _ownedSandboxes
                .UpdateAsync(
                    owner,
                    records => [.. records.Where(record => record.SessionId != session.SessionId)],
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to drop sandbox session {SessionId} from the records of owner {OwnerThreadId}",
                session.SessionId,
                owner
            );
        }
    }

    /// <summary>
    /// Marks <paramref name="session"/> retired in its owner's records. Best-effort, as
    /// <see cref="ForgetOwnedRecordAsync"/> is, and skipped while the owner's conversation is being deleted.
    /// </summary>
    /// <returns>Whether the owner records the session at all: a dedicated sandbox on a host with a store.</returns>
    private async Task<bool> RetireOwnedRecordAsync(SandboxSession session)
    {
        if (_ownedSandboxes is null || session.OwnerThreadId is not { } owner)
        {
            return false;
        }

        if (_ownersBeingDeleted.ContainsKey(owner))
        {
            return true;
        }

        try
        {
            await _ownedSandboxes
                .UpdateAsync(
                    owner,
                    records =>
                        [
                            .. records.Select(record =>
                                record.SessionId == session.SessionId ? record with { Retired = true } : record
                            ),
                        ],
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to record sandbox session {SessionId} of owner {OwnerThreadId} as retired",
                session.SessionId,
                owner
            );
        }

        return true;
    }

    private static bool IsSameSlot(OwnedSandboxRecord record, string workspaceId, string appId, bool readOnlyEgress) =>
        string.Equals(record.WorkspaceId, workspaceId, StringComparison.Ordinal)
        && string.Equals(record.AppId, appId, StringComparison.Ordinal)
        && record.ReadOnlyEgress == readOnlyEgress;
}
