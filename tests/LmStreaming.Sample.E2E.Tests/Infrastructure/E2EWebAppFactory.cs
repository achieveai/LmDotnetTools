using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
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

    public E2EWebAppFactory(string providerMode, ITestAgentBuilder builder)
    {
        if (!string.Equals(providerMode, "test", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(providerMode, "test-anthropic", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"providerMode must be 'test' or 'test-anthropic'; got '{providerMode}'",
                nameof(providerMode));
        }

        _providerMode = providerMode;
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));

        // LmStreaming.Sample reads LM_PROVIDER_MODE at the top of Program.cs — well before any
        // host-builder callback fires. Set it here (in the factory ctor, before Server is
        // accessed) so the sample picks the right test-mode agent factory. Tests must run
        // serialized (see AssemblyInfo.cs) because this env var is process-global.
        Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", _providerMode);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");

        // ConfigureTestServices runs AFTER Program.cs has registered its DefaultTestAgentBuilder,
        // so adding our builder here guarantees it replaces the production default (AddSingleton
        // last-wins semantics when the service is resolved), regardless of whether the sample
        // registers via AddSingleton or TryAddSingleton.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITestAgentBuilder>();
            services.AddSingleton(_builder);
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
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing)
            {
                Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", null);
            }
        }
    }

    /// <summary>
    /// Creates a WebSocket client bound to the in-memory test server and returns a connected
    /// <see cref="System.Net.WebSockets.WebSocket"/> attached to <c>/ws</c>.
    /// </summary>
    public async Task<System.Net.WebSockets.WebSocket> ConnectWebSocketAsync(
        string threadId,
        string? modeId = null,
        CancellationToken ct = default)
    {
        var wsClient = Server.CreateWebSocketClient();

        var query = $"threadId={Uri.EscapeDataString(threadId)}";
        if (!string.IsNullOrEmpty(modeId))
        {
            query += $"&modeId={Uri.EscapeDataString(modeId)}";
        }

        var uri = new UriBuilder(Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "/ws",
            Query = query,
        }.Uri;

        return await wsClient.ConnectAsync(uri, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adapter that plugs a scripted <see cref="HttpMessageHandler"/> into the sample's
    /// test-mode agent path, together with a caller-supplied <see cref="SubAgentOptions"/>
    /// factory.
    /// </summary>
    public sealed class ScriptedBuilder : ITestAgentBuilder
    {
        private readonly HttpMessageHandler _handler;
        private readonly Func<ILoggerFactory, Func<IStreamingAgent>, SubAgentOptions?>? _subAgentFactory;

        public ScriptedBuilder(
            HttpMessageHandler handler,
            Func<ILoggerFactory, Func<IStreamingAgent>, SubAgentOptions?>? subAgentFactory = null)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _subAgentFactory = subAgentFactory;
        }

        public HttpMessageHandler CreateHandler(string providerMode, ILoggerFactory loggerFactory) => _handler;

        public SubAgentOptions? CreateSubAgentOptions(
            ILoggerFactory loggerFactory,
            Func<IStreamingAgent> providerAgentFactory) =>
                _subAgentFactory?.Invoke(loggerFactory, providerAgentFactory);
    }
}
