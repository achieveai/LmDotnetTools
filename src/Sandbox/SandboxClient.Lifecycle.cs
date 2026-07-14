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
    /// The gateway rejected the credential (<see cref="SandboxErrorKind.Authorization"/>) or returned
    /// an unexpected response (<see cref="SandboxErrorKind.Protocol"/>).
    /// </exception>
    public async Task<SandboxInfo> CreateAsync(SandboxCreateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dto = ToWireDto(request, _options.AppId);
        using var response = await SendRestAsync(HttpMethod.Post, "api/v1/sandboxes", dto, sessionId: null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapErrorResponse(response, "sandbox creation");
        }

        var payload = await ReadSandboxResponseOrThrowAsync(response, "sandbox creation", ct).ConfigureAwait(false);
        var info = ToSandboxInfo(payload);
        SeedWorkspaceMountId(info);
        return info;
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
            throw MapErrorResponse(response, $"sandbox '{sessionId}'");
        }

        var payload = await ReadSandboxResponseOrThrowAsync(response, $"sandbox '{sessionId}'", ct).ConfigureAwait(false);
        var info = ToSandboxInfo(payload);
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
        using var response = await SendRestAsync(HttpMethod.Get, "api/v1/sandboxes", body: null, sessionId: null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapErrorResponse(response, "sandbox list");
        }

        ListSandboxesResponseDto? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<ListSandboxesResponseDto>(SandboxJson.RestOptions, ct).ConfigureAwait(false);
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
        var entries = SelectNonNullOrThrow(payload?.Sandboxes, static entry => entry, "sandbox list", (int)response.StatusCode);

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
            throw MapErrorResponse(response, $"deleting sandbox '{sessionId}'");
        }
    }

    /// <summary>
    /// Maps a public <see cref="SandboxCreateRequest"/> to the exact gateway wire shape. Empty
    /// collections become <c>null</c> so <see cref="SandboxJson.RestOptions"/> omits the field
    /// entirely (matching the gateway's "field absent ⇒ apply my default" contract, distinct from
    /// "field present but empty ⇒ select nothing").
    /// </summary>
    private static CreateSandboxRequestDto ToWireDto(SandboxCreateRequest request, string appId) =>
        new(
            App: new AppRefDto(appId),
            Workspace: request.Workspace,
            AuthProviders: request.AuthProviders.Count > 0 ? [.. request.AuthProviders.Select(ToDto)] : null,
            Network: request.NetworkRules.Count > 0 ? new NetworkDto([.. request.NetworkRules.Select(ToDto)]) : null,
            Discovery: request.Discovery is { } discovery
                ? new DiscoveryDto(new DiscoveryWebhookDto(discovery.WebhookUrl, discovery.WebhookAuth))
                : null,
            Marketplaces: request.Marketplaces.Count > 0 ? [.. request.Marketplaces] : null
        );

    private static AuthProviderDto ToDto(SandboxAuthProvider provider) =>
        new(provider.Id, provider.Type, provider.Endpoint, provider.GatewayAuth, provider.CacheTtlSeconds, provider.RequiredScopes);

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

    private static SandboxInfo ToSandboxInfo(CreateSandboxResponseDto dto) =>
        new(dto.SessionId, dto.ContainerId, dto.Volumes?.Workspace?.ContainerPath, dto.Volumes?.Workspace?.Id);

    private static async Task<CreateSandboxResponseDto> ReadSandboxResponseOrThrowAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct
    )
    {
        CreateSandboxResponseDto? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<CreateSandboxResponseDto>(SandboxJson.RestOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a malformed response for {operation}.",
                (int)response.StatusCode,
                ex
            );
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.SessionId))
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a success status but no session id for {operation}.",
                (int)response.StatusCode
            );
        }

        return payload;
    }
}
