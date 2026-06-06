using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;

/// <summary>
///     Acquires a GitHub OAuth token via the GitHub OAuth <em>device flow</em> and caches it on disk
///     for reuse on subsequent runs. Used as a fallback when no existing CLI credential is found.
/// </summary>
/// <remarks>
///     The flow: request a device/user code, present the verification URL + code to the user, then
///     poll the access-token endpoint until the user authorizes. The resulting token (<c>gho_…</c>)
///     is sent directly to the Copilot API — no short-lived exchange is performed.
/// </remarks>
public sealed class DeviceFlowCopilotTokenProvider : ICopilotTokenProvider
{
    // Well-known GitHub Copilot OAuth client id (same one the editor plugins use).
    private const string DefaultClientId = "Iv1.b507a08c87ecfe98";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    private const string TokenCacheFileName = "copilot-oauth-token";

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _scope;
    private readonly Action<DeviceCodeInfo> _present;
    private readonly ILogger _logger;
    private readonly string? _cacheFilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedToken;

    /// <summary>
    ///     Creates a device-flow token provider.
    /// </summary>
    /// <param name="httpClient">HTTP client used for the GitHub OAuth endpoints (injectable for tests).</param>
    /// <param name="clientId">OAuth client id. Defaults to the well-known Copilot client id.</param>
    /// <param name="scope">OAuth scope. Defaults to <c>read:user</c>.</param>
    /// <param name="present">
    ///     Callback that shows the verification URL + user code to the user. Defaults to writing to the console.
    /// </param>
    /// <param name="cacheToDisk">When true (default) the acquired token is cached under local app data.</param>
    /// <param name="logger">Optional logger.</param>
    public DeviceFlowCopilotTokenProvider(
        HttpClient? httpClient = null,
        string? clientId = null,
        string? scope = null,
        Action<DeviceCodeInfo>? present = null,
        bool cacheToDisk = true,
        ILogger? logger = null
    )
    {
        _http = httpClient ?? new HttpClient();
        _clientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId;
        _scope = string.IsNullOrWhiteSpace(scope) ? "read:user" : scope;
        _present = present ?? DefaultPresent;
        _logger = logger ?? NullLogger.Instance;
        _cacheFilePath = cacheToDisk ? ResolveCacheFilePath() : null;
    }

    /// <inheritdoc />
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedToken is not null)
        {
            return _cachedToken;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedToken is not null)
            {
                return _cachedToken;
            }

            var fromDisk = ReadCachedToken();
            if (!string.IsNullOrWhiteSpace(fromDisk))
            {
                _cachedToken = fromDisk;
                return fromDisk;
            }

            var token = await RunDeviceFlowAsync(cancellationToken).ConfigureAwait(false);
            _cachedToken = token;
            WriteCachedToken(token);
            return token;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private async Task<string> RunDeviceFlowAsync(CancellationToken cancellationToken)
    {
        var device = await RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);
        _present(new DeviceCodeInfo(device.UserCode, device.VerificationUri, device.ExpiresIn));

        var interval = TimeSpan.FromSeconds(Math.Max(1, device.Interval));
        var deadline = Stopwatch.StartNew();
        var expiry = TimeSpan.FromSeconds(device.ExpiresIn <= 0 ? 900 : device.ExpiresIn);

        while (deadline.Elapsed < expiry)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            var poll = await PollAccessTokenAsync(device.DeviceCode, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(poll.AccessToken))
            {
                return poll.AccessToken;
            }

            switch (poll.Error)
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    break;
                case "expired_token":
                case "access_denied":
                    throw new InvalidOperationException($"GitHub device-flow authorization failed: {poll.Error}.");
                default:
                    if (!string.IsNullOrEmpty(poll.Error))
                    {
                        throw new InvalidOperationException($"GitHub device-flow authorization failed: {poll.Error}.");
                    }

                    break;
            }
        }

        throw new InvalidOperationException("GitHub device-flow authorization timed out before the user approved it.");
    }

    private async Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["client_id"] = _clientId, ["scope"] = _scope }
            ),
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();
        var body = await response
            .Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken)
            .ConfigureAwait(false);
        return body ?? throw new InvalidOperationException("GitHub device-code endpoint returned an empty response.");
    }

    private async Task<AccessTokenResponse> PollAccessTokenAsync(string deviceCode, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl)
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }
            ),
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response
            .Content.ReadFromJsonAsync<AccessTokenResponse>(cancellationToken)
            .ConfigureAwait(false);
        return body ?? new AccessTokenResponse();
    }

    private static void DefaultPresent(DeviceCodeInfo info)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"To authorize GitHub Copilot access, open {info.VerificationUri} and enter code: {info.UserCode}"
        );
        Console.WriteLine();
    }

    private static string? ResolveCacheFilePath()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LmDotnetTools"
            );
            return Path.Combine(dir, TokenCacheFileName);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private string? ReadCachedToken()
    {
        try
        {
            if (_cacheFilePath is not null && File.Exists(_cacheFilePath))
            {
                var token = File.ReadAllText(_cacheFilePath).Trim();
                return string.IsNullOrWhiteSpace(token) ? null : token;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read cached Copilot token");
        }

        return null;
    }

    private void WriteCachedToken(string token)
    {
        try
        {
            if (_cacheFilePath is null)
            {
                return;
            }

            _ = Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
            File.WriteAllText(_cacheFilePath, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not persist Copilot token cache");
        }
    }

    /// <summary>Details presented to the user to complete device-flow authorization.</summary>
    public sealed record DeviceCodeInfo(string UserCode, string VerificationUri, int ExpiresIn);

    private sealed record DeviceCodeResponse
    {
        [JsonPropertyName("device_code")]
        public string DeviceCode { get; init; } = string.Empty;

        [JsonPropertyName("user_code")]
        public string UserCode { get; init; } = string.Empty;

        [JsonPropertyName("verification_uri")]
        public string VerificationUri { get; init; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("interval")]
        public int Interval { get; init; }
    }

    private sealed record AccessTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }
    }
}
