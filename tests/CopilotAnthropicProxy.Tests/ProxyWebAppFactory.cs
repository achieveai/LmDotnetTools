using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmTestUtils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    /// <summary>Creates a factory whose upstream is driven by <paramref name="upstream"/>.</summary>
    /// <param name="upstream">Fake upstream handler invoked for every forwarded request.</param>
    /// <param name="tokenProvider">Token provider to inject; defaults to a fixed fake token.</param>
    /// <param name="model">The model id the proxy is configured to rewrite every request to.</param>
    /// <param name="idleTimeoutSeconds">
    ///     Optional per-request idle timeout for the proxy (sets <c>COPILOT_ANTHROPIC_IDLE_TIMEOUT_SECONDS</c>);
    ///     used by the 504 test to make a stalled upstream time out quickly.
    /// </param>
    public ProxyWebAppFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> upstream,
        ICopilotTokenProvider? tokenProvider = null,
        string model = ConfiguredModel,
        int? idleTimeoutSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        _upstreamHandler = new FakeHttpMessageHandler(upstream);
        _tokenProvider = tokenProvider ?? new FakeCopilotTokenProvider("fake-token");
        Environment.SetEnvironmentVariable("COPILOT_ANTHROPIC_MODEL", model);
        if (idleTimeoutSeconds is not null)
        {
            Environment.SetEnvironmentVariable(
                "COPILOT_ANTHROPIC_IDLE_TIMEOUT_SECONDS",
                idleTimeoutSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                Environment.SetEnvironmentVariable("COPILOT_ANTHROPIC_IDLE_TIMEOUT_SECONDS", null);
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
