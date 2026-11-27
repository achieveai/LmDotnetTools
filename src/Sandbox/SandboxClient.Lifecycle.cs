using System.Net.Http.Json;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>
    /// Creates a sandbox. <see cref="SandboxCreateRequest.Workspace"/> is forwarded to the gateway
    /// verbatim as a LOGICAL identifier — this method never resolves or creates a host filesystem
    /// path; the gateway (which may be remote) owns workspace directory creation.
    /// </summary>
    /// <exception cref="SandboxException">
    /// The gateway rejected the credential (<see cref="SandboxErrorKind.Authorization"/>), the environment
    /// map (<see cref="SandboxErrorKind.InvalidEnv"/>), or the image
    /// (<see cref="SandboxErrorKind.ImageRejected"/>); the workspace is busy or has only partial backups
    /// (<see cref="SandboxErrorKind.Conflict"/>) or was purged (<see cref="SandboxErrorKind.WorkspacePurged"/>);
    /// an image pull or workspace restore failed (<see cref="SandboxErrorKind.Unavailable"/>); or the gateway
    /// returned an unexpected response (<see cref="SandboxErrorKind.Protocol"/>), including a create that
    /// did not acknowledge the requested <see cref="SandboxCreateRequest.WorkingDir"/> exactly (that sandbox is
    /// deleted before the exception is thrown).
    /// <see cref="SandboxException.ErrorCode"/> names the exact reason; a <c>422 image_unsupported</c>
    /// also carries <see cref="SandboxException.ImageReport"/>.
    /// </exception>
    public async Task<SandboxInfo> CreateAsync(SandboxCreateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dto = ToWireDto(request, _options.AppId);
        var archivePreparation = request.ArchivePreparation;
        if (archivePreparation is not null)
        {
            ValidateArchiveSelection(archivePreparation.Archives, request.Marketplaces, dto.PluginSelection);
        }

        using var response = await SendRestAsync(HttpMethod.Post, "api/v1/sandboxes", dto, sessionId: null, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // The create-time codes (invalid_env with its keys, the image-policy and workspace-backup
            // rejections) all come from the error body, which MapErrorResponseAsync reads for every status
            // except the credential rejections.
            throw await MapErrorResponseAsync(response, "sandbox creation", ct).ConfigureAwait(false);
        }

        // Only session_id must parse before the cleanup scope: without it there is nothing to delete. Every other field,
        // and every content check (source and archive echoes, inventory, image report, skills, ...), is read inside it,
        // so a create whose reply is rejected is deleted, not leaked.
        var statusCode = (int)response.StatusCode;
        var reply = await ReadSandboxReplyJsonAsync(response, "sandbox creation", ct).ConfigureAwait(false);
        var sessionId = ReadSandboxSessionId(reply, "sandbox creation", statusCode);
        SandboxInfo info;
        try
        {
            var payload = ToSandboxReply(reply, "sandbox creation", statusCode)!;
            var archives = ReadResolvedArchiveReceipts(
                payload.ResolvedArchiveReceipts,
                payload.SourceVersion,
                "creating a sandbox",
                statusCode
            );
            if (
                archivePreparation is null
                    ? archives.Count != 0
                    : (
                        archivePreparation.SourceVersion != payload.SourceVersion
                        || !MatchesArchives(archivePreparation.Archives, archives)
                    )
            )
            {
                throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    "The gateway did not acknowledge the exact prepared archive selection.",
                    statusCode
                );
            }

            if (dto.ExpectedSourceVersion is { } expectedVersion && payload.SourceVersion != expectedVersion)
            {
                throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    "The gateway did not acknowledge the expected source version.",
                    statusCode
                );
            }

            info = ToSandboxInfo(
                payload,
                "sandbox creation",
                statusCode,
                archives,
                ReadSourceEcho(payload, "sandbox creation", statusCode),
                sentSearchFlags: request.Skills is { SearchTool: not null } or { AgentsSearchTool: not null }
            );
        }
        catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.Protocol)
        {
            await DeleteRejectedSandboxAsync(sessionId, ex).ConfigureAwait(false);
            throw;
        }

        if (request.WorkingDir is { } workingDir)
        {
            // Before seeding the mount cache: a sandbox whose working directory was not honored is deleted, not used.
            await RequireAcknowledgedWorkingDirAsync(info, workingDir, (int)response.StatusCode, ct)
                .ConfigureAwait(false);
        }

        SeedWorkspaceMountId(info);
        return info;
    }

    /// <summary>
    /// Deletes a sandbox whose create echo was rejected. When the delete fails too, the thrown exception keeps both
    /// causes (the rejection and the delete failure, as an <see cref="AggregateException"/>) and names the session id,
    /// because nothing else holds it any more.
    /// </summary>
    private async Task DeleteRejectedSandboxAsync(string sessionId, SandboxException rejection)
    {
        try
        {
            // Not the caller's token: a cancellation here would orphan the sandbox. The transport budget bounds it.
            await DeleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SandboxException deleteFailure)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"{rejection.Message} Deleting sandbox '{sessionId}' failed ({deleteFailure.Kind}, status "
                    + $"{deleteFailure.StatusCode?.ToString() ?? "none"}); delete it with DeleteAsync.",
                rejection.StatusCode,
                new AggregateException(rejection, deleteFailure)
            );
        }
    }

    /// <summary>
    /// Gets a sandbox by session id. A session owned by a different app id and a genuinely missing
    /// session both surface identically as <see cref="SandboxErrorKind.NotFound"/> — this method
    /// never distinguishes the two, so a caller cannot use it to probe for another app's sessions.
    /// </summary>
    public async Task<SandboxInfo> GetAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var response = await SendRestAsync(
                HttpMethod.Get,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}",
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, $"sandbox '{sessionId}'", ct).ConfigureAwait(false);
        }

        var payload = await ReadSandboxResponseOrThrowAsync(response, $"sandbox '{sessionId}'", ct)
            .ConfigureAwait(false);
        var operation = $"reading sandbox '{sessionId}'";
        var statusCode = (int)response.StatusCode;
        var archives = ReadResolvedArchiveReceipts(
            payload.ResolvedArchiveReceipts,
            payload.SourceVersion,
            operation,
            statusCode
        );
        if (archives.Count != 0 && payload.SessionId != sessionId)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "The gateway returned archive receipts for a different sandbox.",
                statusCode
            );
        }

        var info = ToSandboxInfo(
            payload,
            operation,
            statusCode,
            archives,
            ReadSourceEcho(payload, operation, statusCode)
        );
        SeedWorkspaceMountId(info);
        return info;
    }

    /// <summary>
    /// Lists every sandbox visible to this client's credential. Unlike <see cref="CreateAsync"/> and
    /// <see cref="GetAsync"/>, the gateway's list response is its Docker-level container inventory
    /// (each entry's container id is under <c>id</c>, and there is no <c>volumes</c> field at all —
    /// see <see cref="Wire.ContainerEntryDto"/>), so every <see cref="SandboxInfo"/> this returns has
    /// a <c>null</c> <see cref="SandboxInfo.WorkspaceContainerPath"/>. An entry the gateway hasn't
    /// attributed to any session (<c>session_id: null</c> — e.g. a live but not-yet-assigned
    /// container) is omitted rather than surfaced with a fabricated session id, since every
    /// <see cref="SandboxInfo"/> this SDK hands back must carry a real one to be usable with
    /// <see cref="GetAsync"/>/<see cref="DeleteAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<SandboxInfo>> ListAsync(CancellationToken ct = default)
    {
        using var response = await SendRestAsync(HttpMethod.Get, "api/v1/sandboxes", body: null, sessionId: null, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, "sandbox list", ct).ConfigureAwait(false);
        }

        ListSandboxesResponseDto? payload;
        try
        {
            payload = await response
                .Content.ReadFromJsonAsync<ListSandboxesResponseDto>(SandboxJson.RestOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox gateway returned a malformed sandbox-list response.",
                (int)response.StatusCode,
                ex
            );
        }

        // Reject any null container entry as Protocol before projecting — a JSON `null` element would
        // otherwise NullReference when we read entry.SessionId below. A null-attributed entry
        // (session_id: null) is a valid, expected case (an unassigned/dormant container) and is
        // filtered out, distinct from a null ENTRY (a malformed collection element).
        var entries = SelectNonNullOrThrow(
            payload?.Sandboxes,
            static entry => entry,
            "sandbox list",
            (int)response.StatusCode
        );

        return
        [
            .. entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.SessionId))
                .Select(entry => new SandboxInfo(entry.SessionId!, entry.Id)),
        ];
    }

    /// <summary>
    /// Explicitly deletes a sandbox. This is the ONLY way this SDK ever tears down a sandbox —
    /// disposing a <see cref="SandboxClient"/> never calls this.
    /// </summary>
    public async Task DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var response = await SendRestAsync(
                HttpMethod.Delete,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}",
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, $"deleting sandbox '{sessionId}'", ct).ConfigureAwait(false);
        }

        // The sandbox (and its workspace mount) no longer exist — drop any cached sessionId→mountId entry
        // so a subsequent direct call never replays a dead mapping.
        EvictWorkspaceMountId(sessionId);
    }

    /// <summary>
    /// Maps a public <see cref="SandboxCreateRequest"/> to the exact gateway wire shape. Empty
    /// collections become <c>null</c> so <see cref="SandboxJson.RestOptions"/> omits the field
    /// entirely (matching the gateway's "field absent ⇒ apply my default" contract, distinct from
    /// "field present but empty ⇒ select nothing"). The exceptions send an empty list on purpose:
    /// <see cref="SandboxCreateRequest.PluginSelection"/> (tri-state) and <see cref="SandboxCreateRequest.Marketplaces"/>
    /// under <see cref="SandboxCreateRequest.ExplicitMarketplaces"/>.
    /// </summary>
    private static CreateSandboxRequestDto ToWireDto(SandboxCreateRequest request, string appId) =>
        new(
            App: new AppRefDto(appId),
            Workspace: request.Workspace,
            AuthProviders: request.AuthProviders.Count > 0 ? [.. request.AuthProviders.Select(ToDto)] : null,
            // Always sent, so unmatched egress is denied whatever the gateway's own default is.
            Network: new NetworkDto(
                DefaultAction: "deny",
                Rules: request.NetworkRules.Count > 0 ? [.. request.NetworkRules.Select(ToDto)] : null
            ),
            Discovery: request.Discovery is { } discovery
                ? new DiscoveryDto(new DiscoveryWebhookDto(discovery.WebhookUrl, discovery.WebhookAuth))
                : null,
            // Opt-in exact passthrough: ExplicitMarketplaces sends an empty list as [] (mount none);
            // otherwise empty is omitted and the gateway applies its default set.
            Marketplaces: request.ExplicitMarketplaces || request.Marketplaces.Count > 0
                ? [.. request.Marketplaces]
                : null,
            // Tri-state passthrough: null stays null (field omitted by RestOptions' WhenWritingNull),
            // an empty selection is sent as an explicit empty array.
            PluginSelection: request.PluginSelection is null
                ? null
                : [.. request.PluginSelection.Select(ToPluginRefDto)],
            Env: request.Env.Count > 0 ? request.Env : null,
            Plugins: request.PluginMounts.Count > 0
                ? [.. request.PluginMounts.Select(mount => new PluginMountDto(mount.Path, mount.Name, mount.Origin))]
                : null,
            Image: request.Image,
            WorkingDir: request.WorkingDir,
            Properties: request.Properties.Count > 0 ? request.Properties : null,
            Webhook: request.Webhook is { } webhook
                ? new SandboxWebhookDto(webhook.Url, webhook.AuthHeader, webhook.Events)
                : null,
            Skills: request.Skills is { } skills
                ? new SkillsPolicyDto(
                    skills.DefaultTier is { } tier ? ToWireTier(tier) : null,
                    // Empty is omitted rather than sent as [], which the gateway treats the same.
                    skills.Included.Count > 0
                        ? [.. skills.Included]
                        : null,
                    skills.UnlistedHint,
                    skills.SearchTool,
                    skills.AgentsSearchTool
                )
                : null,
            Mcp: request.Mcp is { } mcp
                ? new McpPolicyDto(
                    mcp.Egress?.ToString().ToLowerInvariant(),
                    mcp.Servers.Count == 0
                        ? null
                        : mcp.Servers.ToDictionary(
                            pair => pair.Key,
                            pair => new McpServerPolicyDto(
                                pair.Value.Auth?.ToString().ToLowerInvariant(),
                                pair.Value.Headers,
                                pair.Value.CacheTtlSeconds,
                                pair.Value.Enabled,
                                pair.Value.Url
                            ),
                            StringComparer.Ordinal
                        ),
                    mcp.Direct.Count == 0
                        ? null
                        : mcp
                            .Direct.Select(server => new DirectMcpServerDto(
                                server.Marketplace,
                                server.Name,
                                server.Url
                            ))
                            .ToArray()
                )
                : null,
            ArchiveReceipts: request.ArchivePreparation?.Archives.Select(ToArchiveReceiptDto).ToArray(),
            ExpectedSourceVersion: ExpectedSourceVersionOf(request),
            // Never `refresh`: preparation consumes it. Omitted when false.
            RequireFresh: request.RequireFresh ? true : null
        );

    private static string? ExpectedSourceVersionOf(SandboxCreateRequest request)
    {
        var archiveVersion = request.ArchivePreparation?.SourceVersion;
        if (request.ExpectedSourceVersion is not { } expected)
        {
            return archiveVersion;
        }

        if (string.IsNullOrWhiteSpace(expected) || expected.Any(char.IsControl))
        {
            throw new ArgumentException(
                "ExpectedSourceVersion cannot be blank or contain control characters.",
                nameof(request)
            );
        }

        return archiveVersion is null || archiveVersion == expected
            ? expected
            : throw new ArgumentException(
                "ExpectedSourceVersion and ArchivePreparation name different source versions.",
                nameof(request)
            );
    }

    private static PluginRefDto ToPluginRefDto(SandboxPluginRef pluginRef) =>
        new(pluginRef.Marketplace, pluginRef.Plugin);

    private static AuthProviderDto ToDto(SandboxAuthProvider provider) =>
        new(
            provider.Id,
            provider.Type,
            provider.Endpoint,
            provider.GatewayAuth,
            provider.CacheTtlSeconds,
            provider.RequiredScopes
        );

    private static NetworkRuleDto ToDto(SandboxNetworkRule rule) =>
        new(
            rule.Id,
            rule.Action,
            rule.Hosts,
            rule.Ports,
            rule.Methods,
            rule.Paths,
            rule.AuthProvider,
            rule.RequiredScopes,
            rule.Priority
        );

    private static SandboxInfo ToSandboxInfo(
        CreateSandboxResponseDto dto,
        string operation,
        int statusCode,
        IReadOnlyList<SandboxAppMarketplaceArchive> archives,
        SourceEcho sourceEcho,
        bool sentSearchFlags = false
    ) =>
        new(
            dto.SessionId,
            dto.ContainerId,
            dto.Volumes?.Workspace?.ContainerPath,
            dto.Volumes?.Workspace?.Id,
            dto.Status,
            ToInventory(dto.Inventory, operation, statusCode),
            ToPluginResolution(dto.PluginResolution, operation, statusCode),
            dto.Volumes?.Workspace?.RelativePath,
            dto.Image,
            dto.ImageDigest,
            dto.ImageReport is null ? null : ToImageReportOrThrow(dto.ImageReport, operation, statusCode),
            dto.Volumes?.Workspace?.WorkingDir,
            dto.Properties,
            dto.Partition is null
                ? null
                : SelectNonNullOrThrow(dto.Partition, static key => key, $"the partition of {operation}", statusCode),
            ToWebhookInfo(dto.Webhook, operation, statusCode),
            ToSkillsPolicy(dto.Skills, operation, statusCode, sentSearchFlags)
        )
        {
            Mcp = TryToMcpResolvedPolicy(dto.Mcp, statusCode),
            SourceVersion = dto.SourceVersion,
            ResolvedArchiveReceipts = archives,
            SourceMode = sourceEcho.Mode,
            SourceReceiptSources = sourceEcho.Sources,
            RawSourceReceipt = sourceEcho.RawReceipt,
            ResolvedMarketplaceRevisions = sourceEcho.Revisions,
        };

    /// <summary>The parsed source mode, receipt and marketplace revisions of a create or GET echo.</summary>
    private readonly record struct SourceEcho(
        string? Mode,
        IReadOnlyList<SandboxSourceReceiptEntry>? Sources,
        JsonElement? RawReceipt,
        IReadOnlyList<SandboxResolvedMarketplaceRevision> Revisions
    );

    /// <summary>Reads the source echo, throwing <see cref="SandboxErrorKind.Protocol"/> when it is malformed.</summary>
    private static SourceEcho ReadSourceEcho(CreateSandboxResponseDto dto, string operation, int statusCode) =>
        new(
            dto.SourceMode switch
            {
                null or { ValueKind: JsonValueKind.Null } => null,
                { ValueKind: JsonValueKind.String } mode => mode.GetString(),
                _ => throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway returned a non-string 'sourceMode' for {operation}.",
                    statusCode
                ),
            },
            dto.SourceReceipt is { ValueKind: not JsonValueKind.Null } receipt
                ? ReadReceiptSources(receipt, operation, statusCode)
                : null,
            dto.SourceReceipt is { ValueKind: not JsonValueKind.Null } raw ? raw.Clone() : null,
            ReadResolvedMarketplaceRevisions(dto.ResolvedMarketplaceRevisions, operation, statusCode)
        );

    private static IReadOnlyList<SandboxResolvedMarketplaceRevision> ReadResolvedMarketplaceRevisions(
        JsonElement? echo,
        string operation,
        int statusCode
    )
    {
        if (echo is not { ValueKind: not JsonValueKind.Null } revisions)
        {
            return [];
        }

        if (revisions.ValueKind != JsonValueKind.Array)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a non-array 'resolvedMarketplaceRevisions' for {operation}.",
                statusCode
            );
        }

        var result = new List<SandboxResolvedMarketplaceRevision>();
        foreach (var revision in revisions.EnumerateArray())
        {
            var marketplace = ReadOptionalString(revision, "marketplace", statusCode, operation);
            var commit = ReadOptionalString(revision, "commit", statusCode, operation);
            if (string.IsNullOrEmpty(marketplace) || string.IsNullOrEmpty(commit))
            {
                throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway returned a resolved marketplace revision without marketplace or commit for {operation}.",
                    statusCode
                );
            }

            result.Add(new SandboxResolvedMarketplaceRevision(marketplace, commit));
        }

        return result.AsReadOnly();
    }

    private static SandboxMcpResolvedPolicy? TryToMcpResolvedPolicy(JsonElement? echo, int statusCode)
    {
        if (echo is null || echo.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        // The optional echo must not hide an otherwise valid session handle. Do not partially map it.
        try
        {
            var dto = echo.Value.Deserialize<McpResolvedPolicyDto>(SandboxJson.RestOptions);
            return dto is null ? null : ToMcpResolvedPolicy(dto, statusCode);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.Protocol)
        {
            return null;
        }
    }

    /// <summary>Maps the GET <c>webhook</c> summary, rejecting one without a URL or flag as <see cref="SandboxErrorKind.Protocol"/>.</summary>
    private static SandboxWebhookInfo? ToWebhookInfo(WebhookSummaryDto? dto, string operation, int statusCode)
    {
        if (dto is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(dto.Url) || dto.AuthHeaderSet is not { } authHeaderSet)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a webhook summary without a url or auth_header_set for {operation}.",
                statusCode
            );
        }

        var events = dto.Events is null
            ? null
            : SelectNonNullOrThrow(dto.Events, static name => name, $"the webhook events of {operation}", statusCode);
        return new SandboxWebhookInfo(dto.Url, events, authHeaderSet);
    }

    /// <summary>
    /// Maps the create response's resolved <c>skills</c> policy. A missing or unknown default tier, or
    /// <c>unavailable</c> (which the gateway never stores), is <see cref="SandboxErrorKind.Protocol"/>. The gateway
    /// leaves a false search flag off its response, and an older gateway has no such field, so absent is ambiguous.
    /// A create that sent a flag settles it: that gateway accepted the fields, so an absent one is off and reusing
    /// the policy keeps it off. Anywhere else it stays <see langword="null"/>.
    /// </summary>
    private static SandboxSkillsPolicy? ToSkillsPolicy(
        SkillsPolicyDto? dto,
        string operation,
        int statusCode,
        bool sentSearchFlags
    )
    {
        if (dto is null)
        {
            return null;
        }

        var included = SelectNonNullOrThrow(
            dto.Included,
            static key => key,
            $"the included skills of {operation}",
            statusCode
        );
        if (
            ParseWireTier(dto.DefaultTier) is not { } tier
            || tier == SandboxSkillTier.Unavailable
            || included.Any(string.IsNullOrWhiteSpace)
        )
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a skills policy with an invalid default_tier or included skill for {operation}.",
                statusCode
            );
        }

        return new SandboxSkillsPolicy(
            tier,
            included,
            dto.UnlistedHint,
            sentSearchFlags ? dto.SearchTool ?? false : dto.SearchTool,
            sentSearchFlags ? dto.AgentsSearchTool ?? false : dto.AgentsSearchTool
        );
    }

    /// <summary>The gateway's lowercase spelling of a tier (<c>skills/tiers.rs</c>, <c>SkillTier::as_str</c>).</summary>
    internal static string ToWireTier(SandboxSkillTier tier) =>
        tier switch
        {
            SandboxSkillTier.Included => "included",
            SandboxSkillTier.Available => "available",
            SandboxSkillTier.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown skill tier."),
        };

    /// <summary>Parses a wire tier, or <see langword="null"/> for an absent or unknown one.</summary>
    internal static SandboxSkillTier? ParseWireTier(string? tier) =>
        tier switch
        {
            "included" => SandboxSkillTier.Included,
            "available" => SandboxSkillTier.Available,
            "unavailable" => SandboxSkillTier.Unavailable,
            _ => null,
        };

    /// <summary>
    /// Maps the gateway's plugin-resolution block. An absent block maps to <see langword="null"/>
    /// rather than a "not supported" resolution: "the gateway never reported one" is a strictly
    /// weaker claim than "the gateway said it does not support filtering".
    /// </summary>
    private static SandboxPluginResolution? ToPluginResolution(
        PluginResolutionDto? dto,
        string operation,
        int statusCode
    ) =>
        dto is null
            ? null
            : new SandboxPluginResolution(
                dto.Supported,
                // Tri-state: a missing "requested" array stays null, it does not become [].
                ToPluginRefs(dto.Requested, "requested", operation, statusCode),
                ToPluginRefs(dto.Effective, "effective", operation, statusCode),
                ToPluginRefs(dto.Failed, "failed", operation, statusCode)
            );

    /// <summary>
    /// Maps one plugin-ref array, rejecting a malformed entry as
    /// <see cref="SandboxErrorKind.Protocol"/> rather than letting it leave the SDK raw.
    /// </summary>
    /// <remarks>
    /// Same rationale as <c>PreviewMarketplacesAsync</c>/<c>ListDiscoveredItemsAsync</c>, and it
    /// applies here for the same reason: <see cref="PluginRefDto"/> models marketplace/plugin as
    /// non-nullable <see langword="string"/>, but <see cref="SandboxJson.RestOptions"/> does not set
    /// <c>RespectNullableAnnotations</c>, so System.Text.Json writes a JSON <c>null</c> into either
    /// one without complaint, and a <c>null</c> ARRAY ELEMENT survives into the list. Unguarded, the
    /// first leaves <see cref="SandboxPluginRef"/>'s constructor as an <see cref="ArgumentException"/>
    /// and the second dereferences to a <see cref="NullReferenceException"/> — a protocol defect
    /// surfacing as an unhandled fault instead of a classified gateway failure.
    ///
    /// <paramref name="field"/> names the offending array in the message. All three are the same
    /// shape, so "malformed plugin resolution" alone would leave the next reader to bisect which of
    /// requested/effective/failed the gateway got wrong.
    ///
    /// A <see langword="null"/> array is NOT an error and is passed through as
    /// <see langword="null"/> — <c>requested</c> is tri-state, and
    /// <see cref="SandboxPluginResolution"/> normalizes effective/failed to empty itself.
    /// </remarks>
    private static IReadOnlyList<SandboxPluginRef>? ToPluginRefs(
        IReadOnlyList<PluginRefDto>? refs,
        string field,
        string operation,
        int statusCode
    )
    {
        if (refs is null)
        {
            return null;
        }

        var entries = SelectNonNullOrThrow(
            refs,
            static entry => entry,
            $"the '{field}' plugin list of {operation}",
            statusCode
        );
        try
        {
            return [.. entries.Select(ToPluginRef)];
        }
        catch (ArgumentException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a plugin resolution whose '{field}' list has an entry with a missing or invalid marketplace/plugin for {operation}.",
                statusCode,
                ex
            );
        }
    }

    private static SandboxPluginRef ToPluginRef(PluginRefDto dto) => new(dto.Marketplace, dto.Plugin);

    /// <summary>
    /// Maps the gateway's inventory block. An absent block maps to <see langword="null"/> so
    /// <see cref="SandboxInfo"/> supplies its "gateway reported none" default — distinct from a
    /// present block that reports <c>unavailable</c> with the gateway's own reason.
    /// </summary>
    /// <remarks>
    /// A malformed ELEMENT and a malformed FIELD are treated differently here, deliberately:
    /// <list type="bullet">
    /// <item>
    /// A <see langword="null"/> array element is a malformed payload and raises
    /// <see cref="SandboxErrorKind.Protocol"/>, like every other null collection element this client
    /// reads. There is no partial item to salvage — the gateway sent a hole where an object belongs,
    /// which is a defect in the response itself, and unguarded it dereferences to a
    /// <see cref="NullReferenceException"/> that escapes this SDK's exception contract entirely.
    /// </item>
    /// <item>
    /// An item whose kind or id is missing/blank is DROPPED, not thrown, because a partially
    /// populated inventory is a tolerated shape: the inventory is a report of what the session
    /// happens to have loaded, so one unidentifiable entry does not invalidate the rest, and
    /// materializing a half-identified item would put an entry with no usable identity in front of
    /// callers.
    /// </item>
    /// </list>
    /// This is the opposite of <see cref="ToPluginRefs"/>, which throws on a blank field — and the
    /// difference is intended, not an oversight to be harmonized away. A plugin resolution reports
    /// what the caller's own explicit selection resolved to, so a malformed entry means the answer
    /// to "what did my selection do" cannot be trusted and silently dropping it would understate the
    /// selection. An inventory answers "what is loaded", where a best-effort list is still useful.
    /// Both behaviours are pinned by tests; changing either is a contract change, not a cleanup.
    /// </remarks>
    private static SandboxInventory? ToInventory(SandboxInventoryDto? dto, string operation, int statusCode)
    {
        if (dto is null)
        {
            return null;
        }

        var items = SelectNonNullOrThrow(dto.Items, static item => item, $"the inventory of {operation}", statusCode)
            .Where(i => !string.IsNullOrWhiteSpace(i.Kind) && !string.IsNullOrWhiteSpace(i.Id))
            .Select(i => new SandboxInventoryItem(i.Kind!, i.Id!, i.Version))
            .ToList();

        return new SandboxInventory(dto.Status, dto.UnavailableReason, items);
    }

    /// <summary>
    /// Reads a create/GET reply in full, typing it in that order (GET and the mount lookup). A malformed body or a
    /// missing session id is <see cref="SandboxErrorKind.Protocol"/>.
    /// </summary>
    private static async Task<CreateSandboxResponseDto> ReadSandboxResponseOrThrowAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct
    )
    {
        var statusCode = (int)response.StatusCode;
        var reply = await ReadSandboxReplyJsonAsync(response, operation, ct).ConfigureAwait(false);
        var payload = ToSandboxReply(reply, operation, statusCode);
        if (payload is null || string.IsNullOrWhiteSpace(payload.SessionId))
        {
            throw NoSessionId(operation, statusCode);
        }

        return payload;
    }

    /// <summary>The reply as untyped JSON, so its fields can be typed one step at a time.</summary>
    private static async Task<JsonElement> ReadSandboxReplyJsonAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct
    )
    {
        try
        {
            return await response
                .Content.ReadFromJsonAsync<JsonElement>(SandboxJson.RestOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw MalformedSandboxReply(operation, (int)response.StatusCode);
        }
    }

    /// <summary>Only the reply's <c>session_id</c>, typed alone so no other field can hide it.</summary>
    private static string ReadSandboxSessionId(JsonElement reply, string operation, int statusCode)
    {
        SandboxSessionIdDto? id;
        try
        {
            id = reply.Deserialize<SandboxSessionIdDto>(SandboxJson.RestOptions);
        }
        catch (JsonException)
        {
            throw MalformedSandboxReply(operation, statusCode);
        }

        return id?.SessionId is { } sessionId && !string.IsNullOrWhiteSpace(sessionId)
            ? sessionId
            : throw NoSessionId(operation, statusCode);
    }

    private static CreateSandboxResponseDto? ToSandboxReply(JsonElement reply, string operation, int statusCode)
    {
        try
        {
            return reply.Deserialize<CreateSandboxResponseDto>(SandboxJson.RestOptions);
        }
        catch (JsonException)
        {
            throw MalformedSandboxReply(operation, statusCode);
        }
    }

    private static SandboxException MalformedSandboxReply(string operation, int statusCode) =>
        new(SandboxErrorKind.Protocol, $"Sandbox gateway returned a malformed response for {operation}.", statusCode);

    private static SandboxException NoSessionId(string operation, int statusCode) =>
        new(
            SandboxErrorKind.Protocol,
            $"Sandbox gateway returned a success status but no session id for {operation}.",
            statusCode
        );
}
