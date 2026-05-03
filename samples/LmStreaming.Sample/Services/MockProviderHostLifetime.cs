using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.MockProviderHost;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Hosts an in-process <see cref="MockProviderHostBuilder"/> Kestrel app on an ephemeral
/// loopback port so the <c>claude-mock</c> / <c>codex-mock</c> / <c>copilot-mock</c> providers
/// can demo all three CLI agents end-to-end without an upstream provider.
/// </summary>
/// <remarks>
/// <para>
/// Registered as both a singleton and an <see cref="IHostedService"/> so it boots eagerly
/// during <c>Host.StartAsync</c>. The bound URL is captured into <see cref="BaseUrl"/> after
/// startup; until startup completes (or if it fails) <see cref="IsRunning"/> stays
/// <c>false</c> and the registry will report the <c>*-mock</c> providers as unavailable.
/// </para>
/// <para>
/// Startup failures are caught and logged so the rest of the sample app still runs — the
/// only consequence of a failure is the three mock providers staying unavailable in the UI.
/// </para>
/// </remarks>
public sealed class MockProviderHostLifetime : IHostedService, IAsyncDisposable
{
    private readonly Func<ScriptedSseResponder> _responderFactory;
    private readonly ILogger<MockProviderHostLifetime> _logger;
    private WebApplication? _app;
    private string? _baseUrl;
    private bool _disposed;

    public MockProviderHostLifetime(ILogger<MockProviderHostLifetime> logger)
        : this(LoadDefaultScenario, logger)
    {
    }

    // Test-only constructor: lets tests inject a stub responder factory so they can verify
    // the lifetime contract without parsing JSON or hitting the filesystem.
    internal MockProviderHostLifetime(
        Func<ScriptedSseResponder> responderFactory,
        ILogger<MockProviderHostLifetime> logger)
    {
        _responderFactory = responderFactory ?? throw new ArgumentNullException(nameof(responderFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Bound base URL, e.g. <c>http://127.0.0.1:5099</c>. Null until startup succeeds.</summary>
    public string? BaseUrl => _baseUrl;

    /// <summary>True after the inner Kestrel app has bound a port. Used by the registry's
    /// availability gate for the <c>*-mock</c> providers.</summary>
    public bool IsRunning => _baseUrl is not null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var responder = _responderFactory();
            _app = MockProviderHostBuilder.Build(
                responder,
                urls: ["http://127.0.0.1:0"]);

            await _app.StartAsync(cancellationToken).ConfigureAwait(false);

            // Kestrel may rewrite a 0-port binding into the actual bound URL; pick the first
            // resolved address.
            _baseUrl = _app.Urls.FirstOrDefault();
            if (_baseUrl is null)
            {
                _logger.LogWarning(
                    "Mock provider host started but did not report a bound URL; *-mock providers will be unavailable");
                return;
            }

            _logger.LogInformation(
                "Mock provider host running at {BaseUrl} — *-mock providers are now selectable",
                _baseUrl);
        }
        catch (Exception ex)
        {
            // Don't propagate: a failed mock host is recoverable (just disables three providers).
            _logger.LogWarning(
                ex,
                "Mock provider host failed to start; *-mock providers will be unavailable");

            if (_app is not null)
            {
                try
                {
                    await _app.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Cleanup-only; primary failure already logged.
                }

                _app = null;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping mock provider host");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_app is not null)
        {
            try
            {
                await _app.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Disposal-only; nothing useful to recover.
            }

            _app = null;
        }
    }

    private static ScriptedSseResponder LoadDefaultScenario()
    {
        var scenario = Environment.GetEnvironmentVariable("LM_MOCK_SCENARIO");
        scenario = string.IsNullOrWhiteSpace(scenario) ? "demo" : scenario;
        return JsonScenarioLoader.Load(scenario);
    }
}
