using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// Boots <c>LmStreaming.Sample</c> on a real Kestrel listener bound to <c>http://127.0.0.1:0</c>
/// so a Playwright-driven browser can navigate to the served SPA. Callers inject an
/// <see cref="ITestAgentBuilder"/> to wire a scripted SSE responder and optional sub-agent
/// templates.
/// </summary>
/// <remarks>
/// <para>
/// The sample's <c>Program.cs</c> uses the minimal-hosting <c>WebApplication.CreateBuilder</c>
/// pattern, so <see cref="WebApplicationFactory{TEntryPoint}"/> routes host creation through
/// its internal deferred-host-builder path. That path hard-casts <see cref="IServer"/> to
/// <see cref="TestServer"/> after <see cref="CreateHost"/> returns — which fails once we
/// swap Kestrel in. We therefore swallow the resulting <see cref="InvalidCastException"/>
/// exactly once inside <c>EnsureStarted</c>; the Kestrel host is already built and started
/// by then, and tests only need <see cref="ServerAddress"/>, not the base class's
/// <c>Server</c>/<c>Services</c> accessors.
/// </para>
/// <para>
/// Canonical pattern: see
/// <see href="https://github.com/martincostello/dotnet-minimal-api-integration-testing"/>.
/// </para>
/// </remarks>
public sealed class BrowserWebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _providerMode;
    private readonly ITestAgentBuilder? _builder;
    private readonly string _conversationPath;
    private readonly int? _fixedPort;
    private readonly IMarketplaceCatalogClient? _catalogClient;
    private readonly HttpMessageHandler? _sandboxGatewayHandler;
    private readonly SandboxGatewayOptions? _sandboxOptions;
    private IHost? _kestrelHost;
    private string? _serverAddress;

    /// <param name="providerMode">Provider-mode key: a scripted mode (<c>test</c> / <c>test-anthropic</c>) or a <c>*-mock</c> variant.</param>
    /// <param name="builder">Scripted SSE agent builder (required for scripted modes; unused/null for <c>*-mock</c> modes).</param>
    /// <param name="fixedPort">
    /// When set, Kestrel binds <c>http://127.0.0.1:{fixedPort}</c> instead of an ephemeral port.
    /// Needed when an external process (e.g. the sandbox gateway) must call back into this host at a
    /// URL known <em>before</em> the host starts — such as the auth webhook for a real <c>git clone</c>
    /// egress test. Defaults to <c>null</c> (ephemeral) for every other scenario.
    /// </param>
    /// <param name="catalogClient">
    /// Optional fake <see cref="IMarketplaceCatalogClient"/> for marketplace scenarios. When set it
    /// replaces the gateway-backed client so the catalog renders with no live gateway; left null for
    /// every other scenario (the real client stays registered and reports the gateway offline).
    /// </param>
    /// <param name="sandboxGatewayHandler">
    /// Optional in-process stand-in for the sandbox gateway HTTP API. When set, the gateway lifetime
    /// and <see cref="SandboxSessionRegistry"/> are rebuilt around it (and the workspace store is
    /// isolated to a temp dir) so a Workspace-Agent turn provisions a sandbox without a live gateway —
    /// letting the test capture the create-sandbox request. Left null for every other scenario.
    /// </param>
    /// <param name="sandboxOptions">
    /// Test <see cref="SandboxGatewayOptions"/> paired with <paramref name="sandboxGatewayHandler"/>
    /// (a temp <c>WorkspaceBasePath</c>, a closed-port <c>BaseUrl</c> so the gateway MCP transport
    /// fails fast and degrades, etc.). Required when a handler is supplied; ignored otherwise.
    /// </param>
    public BrowserWebAppFactory(
        string providerMode,
        ITestAgentBuilder? builder,
        int? fixedPort = null,
        IMarketplaceCatalogClient? catalogClient = null,
        HttpMessageHandler? sandboxGatewayHandler = null,
        SandboxGatewayOptions? sandboxOptions = null)
    {
        // Scripted SSE modes ('test' / 'test-anthropic') drive a fake handler via ITestAgentBuilder.
        // 'claude-mock' (and other *-mock providers) drive the real CLI against the in-process
        // MockProviderHostLifetime that boots automatically at app startup; for those modes the
        // builder is unused and may be null.
        var isScriptedMode =
            string.Equals(providerMode, "test", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerMode, "test-anthropic", StringComparison.OrdinalIgnoreCase);
        var isMockHostMode =
            string.Equals(providerMode, "claude-mock", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerMode, "codex-mock", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerMode, "copilot-mock", StringComparison.OrdinalIgnoreCase);

        if (!isScriptedMode && !isMockHostMode)
        {
            throw new ArgumentException(
                $"providerMode must be 'test', 'test-anthropic', or a *-mock variant; got '{providerMode}'",
                nameof(providerMode)
            );
        }

        if (isScriptedMode && builder is null)
        {
            throw new ArgumentNullException(nameof(builder), "Scripted provider modes require an ITestAgentBuilder.");
        }

        if (sandboxGatewayHandler is not null && sandboxOptions is null)
        {
            throw new ArgumentNullException(
                nameof(sandboxOptions),
                "A sandbox gateway handler requires test SandboxGatewayOptions (temp WorkspaceBasePath etc.).");
        }

        _providerMode = providerMode;
        _builder = builder;
        _fixedPort = fixedPort;
        _catalogClient = catalogClient;
        _sandboxGatewayHandler = sandboxGatewayHandler;
        _sandboxOptions = sandboxOptions;
        _conversationPath = Path.Combine(
            Path.GetTempPath(),
            "lm-streaming-browser-e2e",
            Guid.NewGuid().ToString("N"),
            "conversations"
        );

        // Program.cs reads LM_PROVIDER_MODE at the top-level, well before any host-builder
        // callback fires. Set it here so the sample picks the right test-mode agent factory.
        // Tests run serialized (see AssemblyInfo.cs) because the var is process-global.
        Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", _providerMode);
    }

    /// <summary>
    /// Returns the bound <c>http://127.0.0.1:{port}</c> address. Calling this starts the host
    /// if it has not been started yet.
    /// </summary>
    public string ServerAddress
    {
        get
        {
            EnsureStarted();
            return _serverAddress!;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IConversationStore>();
            services.AddSingleton<IConversationStore>(new FileConversationStore(_conversationPath));

            // ConfigureTestServices runs after Program.cs's default registrations, so
            // AddSingleton + RemoveAll last-wins semantics guarantee our builder is resolved.
            // For *-mock modes the builder is unused (real CLI drives the in-process mock host),
            // so leave the default registration in place when none is supplied.
            if (_builder is not null)
            {
                services.RemoveAll<ITestAgentBuilder>();
                services.AddSingleton(_builder);
            }

            // Swap the gateway-backed catalog client for a fake so marketplace scenarios run with
            // no live gateway. Left untouched when null (non-marketplace tests keep the real client,
            // which simply reports the gateway offline if asked).
            if (_catalogClient is not null)
            {
                services.RemoveAll<IMarketplaceCatalogClient>();
                services.AddSingleton(_catalogClient);
            }

            // Stand-in sandbox gateway: rebuild the gateway lifetime + registry around the capturing
            // handler so a Workspace-Agent turn provisions a sandbox in-process (no live gateway), and
            // isolate the workspace store to a temp dir so test-created workspaces never touch the
            // shared on-disk store. RemoveAll + AddSingleton is last-wins; the hosted-service wrapper
            // (registered as IHostedService) still resolves the replacement lifetime singleton.
            if (_sandboxGatewayHandler is not null)
            {
                var sandboxOptions = _sandboxOptions!;

                services.RemoveAll<SandboxGatewayOptions>();
                services.AddSingleton(sandboxOptions);

                services.RemoveAll<IWorkspaceStore>();
                var workspacesPath = Path.Combine(
                    Path.GetDirectoryName(_conversationPath)!,
                    "workspaces");
                services.AddSingleton<IWorkspaceStore>(
                    new FileWorkspaceStore(workspacesPath, sandboxOptions.ResolveWorkspace().Leaf));

                services.RemoveAll<SandboxGatewayLifetime>();
                services.AddSingleton(sp => new SandboxGatewayLifetime(
                    sp.GetRequiredService<SandboxGatewayOptions>(),
                    sp.GetRequiredService<ILogger<SandboxGatewayLifetime>>(),
                    new HttpClient(_sandboxGatewayHandler, disposeHandler: false)));

                services.RemoveAll<SandboxSessionRegistry>();
                services.AddSingleton(sp => new SandboxSessionRegistry(
                    sp.GetRequiredService<SandboxGatewayLifetime>(),
                    sp.GetRequiredService<SandboxGatewayOptions>(),
                    sp.GetRequiredService<ILogger<SandboxSessionRegistry>>(),
                    new HttpClient(_sandboxGatewayHandler, disposeHandler: false),
                    sp.GetRequiredService<AuthOptions>(),
                    sp.GetRequiredService<AuthSharedSecret>()));
            }
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Idempotent — base.EnsureServer may re-enter after its (TestServer) cast fails
        // below, so skip rebuilding if we already have a running Kestrel host.
        if (_kestrelHost != null)
        {
            return _kestrelHost;
        }

        // Replace TestServer with Kestrel on an ephemeral loopback port. UseKestrel is
        // registered last, so it wins when the DI container resolves IServer.
        builder.ConfigureWebHost(webHost =>
        {
            webHost.UseKestrel();
            webHost.UseUrls($"http://127.0.0.1:{_fixedPort ?? 0}");
        });

        var host = builder.Build();
        host.Start();

        var server = host.Services.GetRequiredService<IServer>();
        var addressesFeature =
            server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose IServerAddressesFeature.");

        _serverAddress =
            addressesFeature.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not bind to any address.");
        _kestrelHost = host;

        return host;
    }

    /// <summary>
    /// Triggers <see cref="WebApplicationFactory{TEntryPoint}.Server"/>, which runs through
    /// <c>EnsureServer → CreateHost</c> (our override builds + starts Kestrel) and then
    /// attempts <c>(TestServer)IServer</c>. We swallow the resulting
    /// <see cref="InvalidCastException"/>: our Kestrel host is already running and we have
    /// the bound URL.
    /// </summary>
    private void EnsureStarted()
    {
        if (_kestrelHost != null)
        {
            return;
        }

        try
        {
            _ = Server;
        }
        catch (InvalidCastException)
        {
            // Expected — base class cannot cast KestrelServer to TestServer. Our CreateHost
            // override has already captured _kestrelHost and _serverAddress by this point.
        }

        if (_kestrelHost == null)
        {
            throw new InvalidOperationException("Kestrel host was not captured — CreateHost override did not run.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && _kestrelHost != null)
            {
                // Short stop timeout so long-lived WebSocket connections don't block teardown.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    _kestrelHost.StopAsync(cts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { }

                _kestrelHost.Dispose();
                _kestrelHost = null;
            }

            var factoryTempDirectory = Directory.GetParent(_conversationPath)?.FullName;
            if (!string.IsNullOrWhiteSpace(factoryTempDirectory) && Directory.Exists(factoryTempDirectory))
            {
                Directory.Delete(factoryTempDirectory, recursive: true);
            }
        }
        finally
        {
            if (disposing)
            {
                Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", null);
            }

            // Base dispose is null-safe on its internal _server/_host, so calling it is fine
            // even though its EnsureServer path threw during startup.
            base.Dispose(disposing);
        }
    }
}
