using System.Net.Http.Json;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Per-app git marketplaces (gateway ADR 0045, <c>/api/v1/apps/self/marketplaces</c>). An app registers a public
/// git repository under an alias once; the gateway clones it and mounts it into the app's sandboxes at
/// <c>/marketplaces/&lt;alias&gt;/</c> next to the operator's marketplaces. Creating a sandbox without
/// <see cref="SandboxCreateRequest.Marketplaces"/> mounts the operator default set plus ALL of the app's
/// registrations; naming aliases mounts just those. <see cref="PreviewMarketplacesAsync"/> lists their plugins.
/// </summary>
/// <remarks>
/// <para>
/// Every request also sends <c>app_id</c> = <see cref="SandboxClientOptions.AppId"/>. With the gateway's
/// <c>AUTH_ENFORCE</c> on, the authenticated app wins and the parameter is ignored; with it off (local development)
/// the gateway requires it (<c>400 app_id_required</c>).
/// </para>
/// <para>
/// Failures (<see cref="SandboxException.ErrorCode"/> always names the reason; the gateway sends no
/// <c>retryable</c> here): <c>404 marketplaces_disabled</c> (the gateway runs without a marketplace store),
/// <c>502 git_failed</c> (clone or fetch failed or timed out) and <c>500 store_failed</c>/<c>registry_failed</c> are
/// <see cref="SandboxErrorKind.Unavailable"/>; <c>409 alias_taken</c> (the alias belongs to an operator
/// marketplace) is <see cref="SandboxErrorKind.Conflict"/>; <c>404 marketplace_not_registered</c> is
/// <see cref="SandboxErrorKind.NotFound"/>; the <c>400</c> rejections (<c>invalid_alias</c>, <c>invalid_url</c>,
/// <c>host_not_allowed</c>, <c>invalid_ref</c>, <c>ref_not_found</c>, <c>app_id_required</c>, <c>invalid_app_id</c>)
/// and the <c>422</c> refusals (<c>unsafe_checkout</c>: the checkout contains a symbolic link;
/// <c>no_default_branch</c>: the remote advertises no default branch for <c>latest</c> to follow) are
/// <see cref="SandboxErrorKind.Protocol"/>.
/// </para>
/// <para>
/// Each sandbox keeps the commit it mounted at create, across syncs and gateway restarts. A sandbox that recorded a
/// commit cannot be resumed on a gateway restarted without its marketplace store; create a new sandbox instead.
/// </para>
/// </remarks>
public sealed partial class SandboxClient
{
    private const string AppMarketplacesPath = "api/v1/apps/self/marketplaces";

    /// <summary>
    /// Registers <paramref name="alias"/> for this app, or re-points it when it already exists (the gateway upserts:
    /// a different <paramref name="url"/> or <paramref name="gitRef"/> replaces the registration and keeps its
    /// <see cref="SandboxAppMarketplace.CreatedAt"/>). The gateway clones or fetches before answering, so a bad URL or
    /// ref fails here rather than at sandbox create, and nothing is saved when it does.
    /// </summary>
    /// <param name="alias">The alias: <c>[A-Za-z0-9_-]</c>, at most 64 characters, not an operator marketplace's.</param>
    /// <param name="url">A public <c>https</c> repository URL on a host the gateway allows.</param>
    /// <param name="gitRef">
    /// A tag, branch or commit to pin, or <see langword="null"/> for <c>latest</c> (follow the default branch).
    /// </param>
    /// <param name="ct">Cancellation token observed by the HTTP call.</param>
    public async Task<SandboxAppMarketplace> RegisterAppMarketplaceAsync(
        string alias,
        string url,
        string? gitRef = null,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var operation = $"registering app marketplace '{alias}'";
        using var response = await SendRestAsync(
                HttpMethod.Put,
                AppMarketplaceUri(alias, suffix: null),
                new AppMarketplaceRegisterDto(url, string.IsNullOrWhiteSpace(gitRef) ? null : gitRef),
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        return await ReadAppMarketplaceRowAsync(response, operation, ct).ConfigureAwait(false);
    }

    /// <summary>Lists this app's registered git marketplaces, as the gateway orders them.</summary>
    public async Task<IReadOnlyList<SandboxAppMarketplace>> ListAppMarketplacesAsync(CancellationToken ct = default)
    {
        const string operation = "listing app marketplaces";
        using var response = await SendRestAsync(
                HttpMethod.Get,
                $"{AppMarketplacesPath}?{AppIdQuery()}",
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, operation, ct).ConfigureAwait(false);
        }

        var statusCode = (int)response.StatusCode;
        var listed = await ReadAppMarketplaceJsonAsync<AppMarketplaceListDto>(response, operation, ct)
            .ConfigureAwait(false);
        if (listed.Marketplaces is null)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned no marketplaces array {operation}.",
                statusCode
            );
        }

        return SelectNonNullOrThrow(
            listed.Marketplaces,
            row => ToAppMarketplace(row, operation, statusCode),
            operation,
            statusCode
        );
    }

    /// <summary>
    /// Fetches <paramref name="alias"/>'s repository now and returns the refreshed registration. A <c>latest</c>
    /// alias moves to the default branch's newest commit (a failed fetch is reported on
    /// <see cref="SandboxAppMarketplace.LastError"/>, the previous commit stays); a pinned alias is returned unchanged.
    /// Running sandboxes keep the commit they mounted. Throws <see cref="SandboxErrorKind.NotFound"/>
    /// (<c>marketplace_not_registered</c>) when the alias is not registered.
    /// </summary>
    public async Task<SandboxAppMarketplace> SyncAppMarketplaceAsync(string alias, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        var operation = $"syncing app marketplace '{alias}'";
        using var response = await SendRestAsync(
                HttpMethod.Post,
                AppMarketplaceUri(alias, suffix: "/sync"),
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        return await ReadAppMarketplaceRowAsync(response, operation, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Unregisters <paramref name="alias"/>. Returns <see langword="false"/> when it was not registered
    /// (<c>404 marketplace_not_registered</c>); any other failure throws. Sandboxes already using it keep their commit.
    /// </summary>
    public async Task<bool> RemoveAppMarketplaceAsync(string alias, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        var operation = $"removing app marketplace '{alias}'";
        using var response = await SendRestAsync(
                HttpMethod.Delete,
                AppMarketplaceUri(alias, suffix: null),
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var error = await MapErrorResponseAsync(response, operation, ct).ConfigureAwait(false);
        return error.ErrorCode == "marketplace_not_registered" ? false : throw error;
    }

    private string AppMarketplaceUri(string alias, string? suffix) =>
        $"{AppMarketplacesPath}/{Uri.EscapeDataString(alias)}{suffix}?{AppIdQuery()}";

    private async Task<SandboxAppMarketplace> ReadAppMarketplaceRowAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct
    )
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, operation, ct).ConfigureAwait(false);
        }

        var row = await ReadAppMarketplaceJsonAsync<AppMarketplaceRowDto>(response, operation, ct)
            .ConfigureAwait(false);
        return ToAppMarketplace(row, operation, (int)response.StatusCode);
    }

    private static async Task<T> ReadAppMarketplaceJsonAsync<T>(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct
    )
        where T : class
    {
        T? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<T>(SandboxJson.RestOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a malformed body {operation}.",
                (int)response.StatusCode,
                ex
            );
        }

        return payload
            ?? throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a success status but no body {operation}.",
                (int)response.StatusCode
            );
    }

    private static SandboxAppMarketplace ToAppMarketplace(AppMarketplaceRowDto row, string operation, int statusCode)
    {
        string Required(string? value, string field) =>
            string.IsNullOrEmpty(value)
                ? throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway returned a marketplace row without '{field}' {operation}.",
                    statusCode
                )
                : value;

        return new SandboxAppMarketplace
        {
            AppId = Required(row.AppId, "app_id"),
            Alias = Required(row.Alias, "alias"),
            Url = Required(row.Url, "url"),
            RepoKey = Required(row.RepoKey, "repo_key"),
            Ref = Required(row.Ref, "ref"),
            ResolvedSha = Required(row.ResolvedSha, "resolved_sha"),
            CreatedAt = Required(row.CreatedAt, "created_at"),
            UpdatedAt = Required(row.UpdatedAt, "updated_at"),
            LastError = row.LastError,
        };
    }
}
