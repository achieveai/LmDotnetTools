using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LmStreaming.Sample.E2E.Tests.Infrastructure;

/// <summary>
/// Factory that boots the <c>LmStreaming.Sample</c> host in-process with a caller-supplied
/// <see cref="ITestAgentBuilder"/>. Tests use this to wire a scripted SSE responder plus
/// optional sub-agent templates, then open a WebSocket to <c>/ws</c> via <see cref="TestServer"/>.
/// </summary>
/// <remarks>
/// The factory sets <c>LM_PROVIDER_MODE</c> on its <c>Server</c> property access so the host
/// selects the test or test-anthropic agent factory. Because <c>Program.cs</c> reads the variable
/// once at startup, callers must pick the mode via the constructor's <c>providerMode</c> argument.
/// </remarks>
public sealed class E2EWebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _providerMode;
    private readonly ITestAgentBuilder _builder;
    private readonly IDictionary<string, string?>? _settings;
    private readonly Action<IServiceCollection>? _configureServices;

    /// <summary>
    /// Cancelled as the FIRST step of <see cref="Dispose(bool)"/>, before the server is torn down.
    /// Every connect helper checks it and links its own token to it, so a connect can never observe
    /// a half-disposed <c>Server</c> (issue #559: <c>ObjectDisposedException</c> out of
    /// <c>ConnectWebSocketAsync</c> when a teardown raced an in-flight connect under CI load) —
    /// it either completes before disposal starts, or fails with a message naming the race.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();

    private int _inFlightConnects;
    private int _disposed;

    public E2EWebAppFactory(
        string providerMode,
        ITestAgentBuilder builder,
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null
    )
    {
        if (
            !string.Equals(providerMode, "test", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(providerMode, "test-anthropic", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new ArgumentException(
                $"providerMode must be 'test' or 'test-anthropic'; got '{providerMode}'",
                nameof(providerMode)
            );
        }

        _providerMode = providerMode;
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _settings = settings;
        _configureServices = configureServices;

        // LmStreaming.Sample reads LM_PROVIDER_MODE at the top of Program.cs — well before any
        // host-builder callback fires. Set it here (in the factory ctor, before Server is
        // accessed) so the sample picks the right test-mode agent factory. Tests must run
        // serialized (see AssemblyInfo.cs) because this env var is process-global.
        Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", _providerMode);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");

        // Per-test configuration overrides (e.g. "Auth:Webhook:HoldTimeoutSeconds") without
        // touching process-global environment variables.
        if (_settings is not null)
        {
            foreach (var (key, value) in _settings)
            {
                builder.UseSetting(key, value);
            }
        }

        // ConfigureTestServices runs AFTER Program.cs has registered its DefaultTestAgentBuilder,
        // so adding our builder here guarantees it replaces the production default (AddSingleton
        // last-wins semantics when the service is resolved), regardless of whether the sample
        // registers via AddSingleton or TryAddSingleton.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITestAgentBuilder>();
            services.AddSingleton(_builder);
            _configureServices?.Invoke(services);
        });
    }

    /// <summary>
    /// Clears the process-global <c>LM_PROVIDER_MODE</c> env var set by the constructor so a
    /// subsequent test (or non-test code running in the same process) does not inherit a
    /// stale provider mode. Tests run serialized (see <c>AssemblyInfo.cs</c>), so this is
    /// safe to do here.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        var firstDisposal = Interlocked.Exchange(ref _disposed, 1) == 0;
        try
        {
            if (disposing && firstDisposal)
            {
                // Teardown must not yank Server out from under an in-flight connect: cancel the
                // lifetime first (which cancels every linked connect token), then wait - bounded,
                // and loud on breach - for those connects to unwind before the base disposes the
                // server. A silent proceed here would recreate the exact ObjectDisposedException
                // race this gate exists to remove.
                _lifetime.Cancel();
                var deadline = Environment.TickCount64 + 10_000;
                while (Volatile.Read(ref _inFlightConnects) > 0)
                {
                    if (Environment.TickCount64 >= deadline)
                    {
                        throw new InvalidOperationException(
                            $"E2EWebAppFactory.Dispose timed out after 10s with "
                                + $"{Volatile.Read(ref _inFlightConnects)} WebSocket connect(s) still in flight "
                                + "despite their tokens being cancelled - a connect is wedged, and disposing the "
                                + "server underneath it would only convert that into an unattributable "
                                + "ObjectDisposedException on some later test."
                        );
                    }

                    Thread.Sleep(10);
                }
            }

            base.Dispose(disposing);
        }
        finally
        {
            if (disposing && firstDisposal)
            {
                _lifetime.Dispose();
                Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", null);
            }
        }
    }

    /// <summary>
    /// Creates a WebSocket client bound to the in-memory test server and returns a connected
    /// <see cref="System.Net.WebSockets.WebSocket"/> attached to <c>/ws</c>.
    /// </summary>
    public Task<System.Net.WebSockets.WebSocket> ConnectWebSocketAsync(
        string threadId,
        string? modeId = null,
        CancellationToken ct = default,
        IEnumerable<string>? subProtocols = null
    )
    {
        var query = $"threadId={Uri.EscapeDataString(threadId)}";
        if (!string.IsNullOrEmpty(modeId))
        {
            query += $"&modeId={Uri.EscapeDataString(modeId)}";
        }

        return ConnectCoreAsync("/ws", query, ct, subProtocols);
    }

    /// <summary>
    /// Creates a WebSocket client bound to the in-memory test server and returns a connected
    /// <see cref="System.Net.WebSockets.WebSocket"/> attached to the FOCUSED sub-agent endpoint
    /// <c>/ws/subagent</c> (WI #194). Mirrors <see cref="ConnectWebSocketAsync"/> but carries the
    /// <c>parentThreadId</c> and <c>agentId</c> query params the route requires (both mandatory —
    /// the route answers 400 when either is missing).
    /// </summary>
    public Task<System.Net.WebSockets.WebSocket> ConnectSubAgentWebSocketAsync(
        string parentThreadId,
        string agentId,
        CancellationToken ct = default,
        IEnumerable<string>? subProtocols = null
    )
    {
        var query = $"parentThreadId={Uri.EscapeDataString(parentThreadId)}&agentId={Uri.EscapeDataString(agentId)}";

        return ConnectCoreAsync("/ws/subagent", query, ct, subProtocols);
    }

    /// <summary>
    /// The one connect path, gated on the factory's lifetime. Registers the connect as in-flight
    /// BEFORE touching <see cref="WebApplicationFactory{TEntryPoint}.Server"/> (whose lazy boot is
    /// exactly where a racing teardown used to surface as a bare <see cref="ObjectDisposedException"/>),
    /// re-checks the lifetime after registering so no disposal can slip between check and register,
    /// and links the caller's token to the lifetime so teardown cancels the connect instead of
    /// disposing the server underneath it.
    /// </summary>
    private async Task<System.Net.WebSockets.WebSocket> ConnectCoreAsync(
        string path,
        string query,
        CancellationToken ct,
        IEnumerable<string>? subProtocols
    )
    {
        Interlocked.Increment(ref _inFlightConnects);
        try
        {
            ThrowIfTornDown();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            try
            {
                var wsClient = Server.CreateWebSocketClient();
                AddSubProtocols(wsClient, subProtocols);

                var uri = new UriBuilder(Server.BaseAddress)
                {
                    Scheme = "ws",
                    Path = path,
                    Query = query,
                }.Uri;

                return await wsClient.ConnectAsync(uri, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Deliberately unreachable in a healthy test (Dispose drains in-flight connects
                // before proceeding, so only a connect STARTED before Dispose and cancelled BY it
                // lands here). Rethrown with the race named so a future regression reads as what it
                // is, not as a mystery ObjectDisposedException.
                ThrowIfTornDown();
                throw;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightConnects);
        }
    }

    /// <summary>Names the teardown/connect race loudly instead of letting a disposed server speak.</summary>
    private void ThrowIfTornDown()
    {
        if (_lifetime.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"WebSocket connect to the {nameof(E2EWebAppFactory)} was attempted during or after its "
                    + "disposal - the test (or a task it leaked) is connecting while teardown is running. "
                    + "Keep every connect awaited before the factory's using-scope ends."
            );
        }
    }

    /// <summary>
    /// Offers <paramref name="subProtocols"/> on the handshake. This is how a browser presents a
    /// credential to a WebSocket endpoint - the WebSocket API admits no custom headers, so the
    /// <c>Sec-WebSocket-Protocol</c> list is the only header a page can influence.
    /// </summary>
    private static void AddSubProtocols(WebSocketClient client, IEnumerable<string>? subProtocols)
    {
        if (subProtocols is null)
        {
            return;
        }

        foreach (var subProtocol in subProtocols)
        {
            client.SubProtocols.Add(subProtocol);
        }
    }
}
