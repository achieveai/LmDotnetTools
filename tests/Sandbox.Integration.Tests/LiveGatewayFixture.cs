using System.Net;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// Shared connection to a LIVE pinned sandbox gateway, configured purely from environment so the
/// same credential the operator seeds into the gateway (<c>APP_SECRETS</c>) flows into the SDK.
/// </summary>
/// <remarks>
/// <para>
/// Prerequisites (all three required to run the live matrix):
/// <list type="bullet">
///   <item><c>SANDBOX_BASE_URL</c> — absolute gateway address, e.g. <c>http://127.0.0.1:3987</c>.</item>
///   <item><c>SANDBOX_APP_ID</c> — the app id registered in the gateway's <c>APP_SECRETS</c> registry.</item>
///   <item><c>SANDBOX_APP_KEY</c> — that app id's base64 secret (the <c>X-Sbx-App-Key</c> value).</item>
/// </list>
/// </para>
/// <para>
/// <b>Fail-when-required, opt-out-locally.</b> When <c>SANDBOX_CONTRACT_REQUIRED</c> is truthy
/// (<c>true/1/yes/on</c>) — the CI contract job sets it — a missing variable or an unreachable
/// <c>/health</c> is a HARD FAILURE (the fixture throws, erroring every test) rather than a silent
/// skip or a mock substitution. When it is unset, the same missing prerequisites mark the matrix
/// unavailable and every test SKIPS, so a routine local <c>dotnet test</c> without a gateway is green.
/// </para>
/// </remarks>
public sealed class LiveGatewayFixture : IAsyncLifetime
{
    /// <summary>Non-null when the live matrix cannot run; the reason each test surfaces via <see cref="Skip"/>.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>The credential-scoped SDK client bound to the live gateway; only valid when <see cref="SkipReason"/> is null.</summary>
    public SandboxClient Client { get; private set; } = null!;

    /// <summary>The authenticated app id in play, echoed back by the gateway on create responses.</summary>
    public string AppId { get; private set; } = string.Empty;

    private SandboxClient? _ownedClient;

    public async Task InitializeAsync()
    {
        var required = IsTruthy(Environment.GetEnvironmentVariable("SANDBOX_CONTRACT_REQUIRED"));
        var baseUrl = Trimmed(Environment.GetEnvironmentVariable("SANDBOX_BASE_URL"));
        var appId = Trimmed(Environment.GetEnvironmentVariable("SANDBOX_APP_ID"));
        var appKey = Trimmed(Environment.GetEnvironmentVariable("SANDBOX_APP_KEY"));

        var missing = new List<string>();
        if (baseUrl.Length == 0)
        {
            missing.Add("SANDBOX_BASE_URL");
        }

        if (appId.Length == 0)
        {
            missing.Add("SANDBOX_APP_ID");
        }

        if (appKey.Length == 0)
        {
            missing.Add("SANDBOX_APP_KEY");
        }

        if (missing.Count > 0)
        {
            Unavailable(required, $"missing environment: {string.Join(", ", missing)}");
            return;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serverAddress))
        {
            Unavailable(required, $"SANDBOX_BASE_URL is not an absolute URI: '{baseUrl}'");
            return;
        }

        // Readiness is checked against the UNAUTHENTICATED /health probe only — never an app route,
        // so a wrong credential is diagnosed by the tests, not masked as an availability problem.
        var healthy = await ProbeHealthAsync(serverAddress).ConfigureAwait(false);
        if (!healthy)
        {
            Unavailable(required, $"gateway /health not reachable at {serverAddress}");
            return;
        }

        var isHttp = string.Equals(serverAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var options = new SandboxClientOptions(
            serverAddress,
            appId,
            appKey,
            executionTimeout: TimeSpan.FromSeconds(60),
            transportTimeout: TimeSpan.FromSeconds(30),
            allowInsecureDevelopmentTransport: isHttp
        );

        _ownedClient = new SandboxClient(options);
        Client = _ownedClient;
        AppId = appId;
    }

    public Task DisposeAsync()
    {
        _ownedClient?.Dispose();
        return Task.CompletedTask;
    }

    private void Unavailable(bool required, string reason)
    {
        if (required)
        {
            throw new InvalidOperationException(
                $"SANDBOX_CONTRACT_REQUIRED is set but the live gateway is not usable: {reason}. "
                    + "The contract job must run against a live pinned gateway — it never silently skips or mocks."
            );
        }

        SkipReason = $"Live sandbox gateway unavailable ({reason}). Set SANDBOX_BASE_URL/SANDBOX_APP_ID/SANDBOX_APP_KEY "
            + "(and SANDBOX_CONTRACT_REQUIRED to make absence fail) to run the live contract matrix.";
    }

    private static async Task<bool> ProbeHealthAsync(Uri serverAddress)
    {
        using var http = new HttpClient { BaseAddress = serverAddress, Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync("health").ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static string Trimmed(string? value) => value?.Trim() ?? string.Empty;

    private static bool IsTruthy(string? value) =>
        value is not null
        && (
            value.Trim() is "1"
            || string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "on", StringComparison.OrdinalIgnoreCase)
        );
}

[CollectionDefinition(Name)]
public sealed class LiveGatewayCollection : ICollectionFixture<LiveGatewayFixture>
{
    public const string Name = "LiveGateway";
}
