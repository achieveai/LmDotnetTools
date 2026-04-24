using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;
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
    private readonly ITestAgentBuilder _builder;
    private IHost? _kestrelHost;
    private string? _serverAddress;

    public BrowserWebAppFactory(string providerMode, ITestAgentBuilder builder)
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

        // ConfigureTestServices runs after Program.cs's default registrations, so
        // AddSingleton + RemoveAll last-wins semantics guarantee our builder is resolved.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITestAgentBuilder>();
            services.AddSingleton(_builder);
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
            webHost.UseUrls("http://127.0.0.1:0");
        });

        var host = builder.Build();
        host.Start();

        var server = host.Services.GetRequiredService<IServer>();
        var addressesFeature =
            server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException(
                "Kestrel did not expose IServerAddressesFeature."
            );

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
            throw new InvalidOperationException(
                "Kestrel host was not captured — CreateHost override did not run."
            );
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

    /// <summary>
    /// Adapter that plugs a scripted <see cref="HttpMessageHandler"/> into the sample's
    /// test-mode agent path, together with a caller-supplied <see cref="SubAgentOptions"/>
    /// factory. Identical shape to the non-browser E2E factory's builder so test code can
    /// share scripting helpers.
    /// </summary>
    public sealed class ScriptedBuilder : ITestAgentBuilder
    {
        private readonly HttpMessageHandler _handler;
        private readonly Func<
            ILoggerFactory,
            Func<IStreamingAgent>,
            SubAgentOptions?
        >? _subAgentFactory;

        public ScriptedBuilder(
            HttpMessageHandler handler,
            Func<ILoggerFactory, Func<IStreamingAgent>, SubAgentOptions?>? subAgentFactory = null
        )
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _subAgentFactory = subAgentFactory;
        }

        public HttpMessageHandler CreateHandler(string providerMode, ILoggerFactory loggerFactory) =>
            _handler;

        public SubAgentOptions? CreateSubAgentOptions(
            ILoggerFactory loggerFactory,
            Func<IStreamingAgent> providerAgentFactory
        ) => _subAgentFactory?.Invoke(loggerFactory, providerAgentFactory);
    }
}
