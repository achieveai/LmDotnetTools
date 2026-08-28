using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Web.Jina;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>
///     Boots the <c>CopilotAnthropicProxy.Sample</c> host in-process with a fake upstream
///     <see cref="HttpMessageHandler"/> and a fake <see cref="ICopilotTokenProvider"/>, so the whole
///     proxy pipeline (guard, model rewrite, header allowlist, response copy, streaming) is exercised
///     over real HTTP without ever calling GitHub Copilot.
/// </summary>
/// <remarks>
///     The sample reads <c>COPILOT_ANTHROPIC_MODEL</c> at the very top of <c>Program.cs</c>, so the
///     value is set in the constructor (before <see cref="WebApplicationFactory{TEntryPoint}.Server"/>
///     is first accessed) and cleared on dispose. Tests run serialized (see <c>AssemblyInfo.cs</c>)
///     because that env var is process-global.
/// </remarks>
public sealed class ProxyWebAppFactory : WebApplicationFactory<Program>
{
    /// <summary>The model id the factory configures; the proxy rewrites every request to this id.</summary>
    public const string ConfiguredModel = "copilot-claude-opus-4.8";

    private readonly HttpMessageHandler _upstreamHandler;
    private readonly ICopilotTokenProvider _tokenProvider;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _jinaUpstream;

    /// <summary>Creates a factory whose upstream is driven by <paramref name="upstream"/>.</summary>
    /// <param name="upstream">Fake upstream handler invoked for every forwarded request.</param>
    /// <param name="tokenProvider">Token provider to inject; defaults to a fixed fake token.</param>
    /// <param name="model">
    ///     The model id the proxy is configured to pin every request to. Pass <c>null</c> to leave
    ///     <c>COPILOT_ANTHROPIC_MODEL</c> unset, exercising the discovery path instead (the proxy then
    ///     calls <c>GET /models</c> on <paramref name="upstream"/> at startup) — the caller's
    ///     <paramref name="upstream"/> must handle that request too in that mode.
    /// </param>
    /// <param name="idleTimeoutSeconds">
    ///     Optional per-request idle timeout for the proxy (sets <c>COPILOT_ANTHROPIC_IDLE_TIMEOUT_SECONDS</c>);
    ///     used by the 504 test to make a stalled upstream time out quickly.
    /// </param>
    /// <param name="keepAliveSeconds">
    ///     Optional downstream SSE keep-alive interval (sets <c>COPILOT_ANTHROPIC_KEEPALIVE_SECONDS</c>);
    ///     used by the keep-alive test to make pings fire quickly against a silent upstream.
    /// </param>
    /// <param name="maxBodyBytes">
    ///     Optional cap on a buffered body (sets <c>COPILOT_ANTHROPIC_MAX_BODY_BYTES</c>); used by the
    ///     oversized-reply test so a few kilobytes stand in for the 32 MB production default.
    /// </param>
    /// <param name="modelEndpoints">
    ///     Optional pinned-model capability metadata (sets <c>COPILOT_ANTHROPIC_MODEL_ENDPOINTS</c>);
    ///     only meaningful alongside a non-null <paramref name="model"/>, which otherwise has no
    ///     discovered endpoint list.
    /// </param>
    /// <param name="jinaApiKey">Optional Jina key that enables local MCP web-tool fallback.</param>
    /// <param name="webToolsOutputCap">Optional local web-tool output character cap.</param>
    /// <param name="webToolsTimeoutMs">Optional local web-tool timeout in milliseconds.</param>
    /// <param name="jinaUpstream">Optional fake Jina transport, isolated from the fake Copilot transport.</param>
    public ProxyWebAppFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> upstream,
        ICopilotTokenProvider? tokenProvider = null,
        string? model = ConfiguredModel,
        int? idleTimeoutSeconds = null,
        int? keepAliveSeconds = null,
        long? maxBodyBytes = null,
        string? modelEndpoints = null,
        string? jinaApiKey = null,
        int? webToolsOutputCap = null,
        int? webToolsTimeoutMs = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? jinaUpstream = null
    )
    {
        ArgumentNullException.ThrowIfNull(upstream);

        _upstreamHandler = new FakeHttpMessageHandler(upstream);
        _tokenProvider = tokenProvider ?? new FakeCopilotTokenProvider("fake-token");
        _jinaUpstream = jinaUpstream;
        Environment.SetEnvironmentVariable("COPILOT_ANTHROPIC_MODEL", model);
        Environment.SetEnvironmentVariable("JINA_API_KEY", jinaApiKey);
        Environment.SetEnvironmentVariable(
            "WEB_TOOLS_OUTPUT_CAP",
            webToolsOutputCap?.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        Environment.SetEnvironmentVariable(
            "WEB_TOOLS_TIMEOUT_MS",
            webToolsTimeoutMs?.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        Environment.SetEnvironmentVariable("COPILOT_ANTHROPIC_MODEL_ENDPOINTS", modelEndpoints);
        if (idleTimeoutSeconds is not null)
        {
            Environment.SetEnvironmentVariable(
                "COPILOT_ANTHROPIC_IDLE_TIMEOUT_SECONDS",
                idleTimeoutSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
        }

        if (keepAliveSeconds is not null)
        {
            Environment.SetEnvironmentVariable(
                "COPILOT_ANTHROPIC_KEEPALIVE_SECONDS",
                keepAliveSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
        }

        if (maxBodyBytes is not null)
        {
            Environment.SetEnvironmentVariable(
                "COPILOT_ANTHROPIC_MAX_BODY_BYTES",
                maxBodyBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");

        // Runs AFTER Program.cs registers its real services, so RemoveAll + AddSingleton guarantees the
        // fakes win regardless of AddSingleton/TryAddSingleton ordering in the sample.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICopilotTokenProvider>();
            services.AddSingleton(_tokenProvider);

            services.RemoveAll<HttpMessageHandler>();
            services.AddSingleton(_upstreamHandler);

            if (_jinaUpstream is not null)
            {
                services.RemoveAll<JinaWebProvider>();
                services.AddSingleton(sp => new JinaWebProvider(
                    new HttpClient(new FakeHttpMessageHandler(_jinaUpstream)),
                    sp.GetRequiredService<WebToolsOptions>(),
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<JinaWebProvider>()
                ));
                services.RemoveAll<McpJinaToolCatalog>();
                services.AddSingleton<McpJinaToolCatalog>();
                services.RemoveAll<McpToolComposition>();
                services.AddSingleton<McpToolComposition>();
            }
        });
    }

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
                Environment.SetEnvironmentVariable("COPILOT_ANTHROPIC_MODEL", null);
                Environment.SetEnvironmentVariable("COPILOT_ANTHROPIC_MODEL_ENDPOINTS", null);
                Environment.SetEnvironmentVariable("COPILOT_ANTHROPIC_IDLE_TIMEOUT_SECONDS", null);
                Environment.SetEnvironmentVariable("COPILOT_ANTHROPIC_KEEPALIVE_SECONDS", null);
                Environment.SetEnvironmentVariable("COPILOT_ANTHROPIC_MAX_BODY_BYTES", null);
                Environment.SetEnvironmentVariable("JINA_API_KEY", null);
                Environment.SetEnvironmentVariable("WEB_TOOLS_OUTPUT_CAP", null);
                Environment.SetEnvironmentVariable("WEB_TOOLS_TIMEOUT_MS", null);
            }
        }
    }
}

/// <summary>A fixed-token (or always-throwing) <see cref="ICopilotTokenProvider"/> for tests.</summary>
public sealed class FakeCopilotTokenProvider : ICopilotTokenProvider
{
    private readonly string? _token;

    /// <summary>Returns <paramref name="token"/>; pass null to simulate an acquisition failure.</summary>
    public FakeCopilotTokenProvider(string? token) => _token = token;

    public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) =>
        _token is not null
            ? Task.FromResult(_token)
            : throw new InvalidOperationException("No GitHub Copilot token found (test).");
}
