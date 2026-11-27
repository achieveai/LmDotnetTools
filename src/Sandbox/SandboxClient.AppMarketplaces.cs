using System.Net.Http.Json;
using System.Text;
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
    ) =>
        await PutAppMarketplaceAsync(alias, url, string.IsNullOrWhiteSpace(gitRef) ? null : gitRef, null, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Registers <paramref name="alias"/> from a <see cref="SandboxAppMarketplaceRegistration"/>, which can name a
    /// private repository through an opaque <see cref="SandboxAppMarketplaceRegistration.ConnectionId"/>. The gateway
    /// asks the app's credential broker (configured by the gateway operator, owned by the host) for a short-lived read
    /// credential; this SDK never sees it. A private registration is never retried anonymously.
    /// </summary>
    /// <remarks>
    /// Besides the failures listed on the class: <c>403 marketplace_access_denied</c> (the broker refused this app,
    /// connection or repository) is <see cref="SandboxErrorKind.Authorization"/> WITH
    /// <see cref="SandboxException.ErrorCode"/> set, while a rejected app credential is Authorization without one;
    /// <c>503 marketplace_broker_unavailable</c> is <see cref="SandboxErrorKind.Unavailable"/>. Needs a gateway
    /// containing PR #375.
    /// </remarks>
    /// <exception cref="ArgumentException">The URL is blank or the connection id breaks its character rule.</exception>
    public async Task<SandboxAppMarketplace> RegisterAppMarketplaceAsync(
        string alias,
        SandboxAppMarketplaceRegistration registration,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.ConnectionId is { } connectionId && !IsConnectionId(connectionId))
        {
            throw new ArgumentException(
                "A connection id is 1 to 128 characters of letters, digits, '_', '.' or '-'.",
                nameof(registration)
            );
        }

        return await PutAppMarketplaceAsync(
                alias,
                registration.Url,
                string.IsNullOrWhiteSpace(registration.Ref) ? null : registration.Ref,
                plugin: null,
                ct,
                registration.ConnectionId
            )
            .ConfigureAwait(false);
    }

    private static bool IsConnectionId(string value) =>
        value.Length is >= 1 and <= 128
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    /// <summary>Registers only one catalog package at a full Git commit.</summary>
    public async Task<SandboxAppMarketplace> RegisterAppMarketplacePackageAsync(
        string alias,
        string url,
        string commit,
        string plugin,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plugin);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        if (commit.Length != 40 || !commit.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "A package registration requires a full 40-character Git commit.",
                nameof(commit)
            );
        }

        var row = await PutAppMarketplaceAsync(alias, url, commit, plugin, ct).ConfigureAwait(false);
        if (
            row.Alias != alias
            || row.Plugin != plugin
            || row.LastError is not null
            || !string.Equals(row.Ref, commit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(row.ResolvedSha, commit, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "The sandbox gateway did not acknowledge the isolated package and commit."
            );
        }

        return row;
    }

    private async Task<SandboxAppMarketplace> PutAppMarketplaceAsync(
        string alias,
        string url,
        string? gitRef,
        string? plugin,
        CancellationToken ct,
        string? connectionId = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var operation = $"registering app marketplace '{alias}'";
        using var response = await SendRestAsync(
                HttpMethod.Put,
                AppMarketplaceUri(alias, suffix: plugin is null ? null : "/package"),
                new AppMarketplaceRegisterDto(url, gitRef, plugin, connectionId),
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        return await ReadAppMarketplaceRowAsync(response, operation, ct).ConfigureAwait(false);
    }

    /// <summary>Stages one publisher archive by its HTTPS URL, download digest, and exact package name.</summary>
    public async Task<SandboxAppMarketplaceArchive> RegisterAppMarketplaceArchiveAsync(
        string alias,
        string url,
        string sha256,
        string plugin,
        CancellationToken ct = default
    )
    {
        ValidateArchiveAlias(alias);
        if (!IsArchivePlugin(plugin))
        {
            throw new ArgumentException(
                "An archive requires an exact package name up to 256 UTF-8 bytes.",
                nameof(plugin)
            );
        }

        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentNullException.ThrowIfNull(url);
        if (!IsArchiveUrl(url))
        {
            throw new ArgumentException("A publisher archive requires a fixed HTTPS URL.", nameof(url));
        }

        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A publisher archive requires a full SHA-256 digest.", nameof(sha256));
        }

        var operation = $"registering publisher archive '{alias}'";
        var request = new AppMarketplaceArchiveRegisterDto(url, sha256.ToLowerInvariant(), plugin);
        using var response = await SendRestAsync(
                HttpMethod.Put,
                AppMarketplaceArchiveUri(alias),
                request,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, operation, ct).ConfigureAwait(false);
        }

        var receipt = await ReadArchiveReceiptAsync(response, alias, operation, ct).ConfigureAwait(false);
        if (receipt.Url != request.Url || receipt.Sha256 != request.Sha256 || receipt.Plugin != request.Plugin)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "The sandbox gateway acknowledged a different publisher archive.",
                (int)response.StatusCode
            );
        }

        return receipt;
    }

    /// <summary>Reads and validates a signed app's archive registration. An unknown alias returns null.</summary>
    public async Task<SandboxAppMarketplaceArchive?> GetAppMarketplaceArchiveAsync(
        string alias,
        CancellationToken ct = default
    )
    {
        ValidateArchiveAlias(alias);

        var operation = $"reading publisher archive '{alias}'";
        using var response = await SendRestAsync(
                HttpMethod.Get,
                AppMarketplaceArchiveUri(alias),
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await MapErrorResponseAsync(response, operation, ct).ConfigureAwait(false);
            return error.ErrorCode == "marketplace_not_registered" ? null : throw error;
        }

        return await ReadArchiveReceiptAsync(response, alias, operation, ct).ConfigureAwait(false);
    }

    private static string AppMarketplaceArchiveUri(string alias) =>
        $"{AppMarketplacesPath}/{Uri.EscapeDataString(alias)}/archive";

    private async Task<SandboxAppMarketplaceArchive> ReadArchiveReceiptAsync(
        HttpResponseMessage response,
        string alias,
        string operation,
        CancellationToken ct
    )
    {
        var row = await ReadAppMarketplaceJsonAsync<AppMarketplaceArchiveReceiptDto>(response, operation, ct)
            .ConfigureAwait(false);
        var receipt = ReadArchiveReceipt(row, operation, (int)response.StatusCode);
        if (receipt.Alias != alias)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a different alias {operation}.",
                (int)response.StatusCode
            );
        }

        return receipt;
    }

    private SandboxAppMarketplaceArchive ReadArchiveReceipt(
        AppMarketplaceArchiveReceiptDto? row,
        string operation,
        int statusCode
    )
    {
        if (
            row?.Kind != "archive"
            || row.AppId != _options.AppId
            || row.Alias is null
            || !IsArchiveAlias(row.Alias)
            || row.Url is null
            || !IsArchiveUrl(row.Url)
            || row.Sha256 is not { Length: 64 }
            || !row.Sha256.All(IsLowerHexDigit)
            || row.ContentSha256 is not { Length: 64 }
            || !row.ContentSha256.All(IsLowerHexDigit)
            || row.Plugin is null
            || !IsArchivePlugin(row.Plugin)
        )
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned an invalid receipt {operation}.",
                statusCode
            );
        }

        return new SandboxAppMarketplaceArchive(
            row.AppId,
            row.Alias,
            row.Url,
            row.Sha256,
            row.ContentSha256,
            row.Plugin
        );
    }

    private static void ValidateArchiveAlias(string? alias)
    {
        if (!IsArchiveAlias(alias))
        {
            throw new ArgumentException(
                "An archive alias requires 1 to 64 letters, digits, hyphens, or underscores.",
                nameof(alias)
            );
        }
    }

    private static bool IsArchiveAlias(string? alias) =>
        alias is { Length: > 0 and <= 64 } && alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsArchivePlugin(string? plugin) =>
        !string.IsNullOrWhiteSpace(plugin)
        && plugin == plugin.Trim()
        && !plugin.Any(char.IsControl)
        && Encoding.UTF8.GetByteCount(plugin) <= 256;

    private static bool IsArchiveUrl(string? url)
    {
        const string scheme = "https://";
        if (
            url is not { Length: > 0 and <= 2048 }
            || !url.StartsWith(scheme, StringComparison.Ordinal)
            || !url.All(character => character is >= '!' and <= '~' and not ('?' or '#' or '\\'))
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.HostNameType != UriHostNameType.Dns
            || !IsArchiveHost(uri.IdnHost)
        )
        {
            return false;
        }

        var pathStart = url.IndexOf('/', scheme.Length);
        if (pathStart == -1 || url[..pathStart] != scheme + uri.IdnHost)
        {
            return false;
        }

        // Preserve publisher path escapes. System.Uri decodes some unreserved bytes that the gateway retains.
        var path = url[pathStart..];
        return !path.Any(character => character is '"' or '<' or '>' or '`' or '{' or '}')
            && !path.Split('/')
                .Any(segment => segment.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase) is "." or "..");
    }

    private static bool IsArchiveHost(string host) =>
        host.Length <= 253
        && host.Contains('.')
        && !host.EndsWith(".localhost", StringComparison.Ordinal)
        && !host.EndsWith(".local", StringComparison.Ordinal)
        && host.Split('.')
            .All(label =>
                label.Length is > 0 and <= 63
                && label[0] != '-'
                && label[^1] != '-'
                && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            );

    private static bool IsLowerHexDigit(char value) => value is (>= '0' and <= '9') or (>= 'a' and <= 'f');

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
            Plugin = row.Plugin,
            CreatedAt = Required(row.CreatedAt, "created_at"),
            UpdatedAt = Required(row.UpdatedAt, "updated_at"),
            LastError = row.LastError,
        };
    }
}
