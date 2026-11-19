using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Boots the real <c>Program</c> host with only the sandbox gateway swapped for an in-memory handler, the marketplace
/// catalog emptied, the sandbox MCP transport failing at once, and the conversation and workspace stores in a temp
/// directory this factory owns and deletes. The registry, the pool, the agent factory, the controllers and the stores
/// are the production types. Shared by the composition tests of per-conversation sandbox settings, which differ only
/// in their gateway and configuration.
/// </summary>
internal sealed class FakeGatewayWebAppFactory : WebApplicationFactory<Program>
{
    private readonly HttpMessageHandler _gateway;
    private readonly string _appId;
    private readonly IReadOnlyDictionary<string, string> _settings;
    private readonly bool _ownsRoot;

    /// <param name="gateway">Answers every sandbox gateway call: creates, deletes, liveness and operations.</param>
    /// <param name="appId">The host's sandbox gateway app id.</param>
    /// <param name="settings">Further host settings, such as egress rules.</param>
    /// <param name="root">
    /// The stores' directory, to boot a host over the stores an earlier one left (a restart). The caller then owns
    /// and deletes it. Null for a fresh directory this factory owns.
    /// </param>
    public FakeGatewayWebAppFactory(
        HttpMessageHandler gateway,
        string appId,
        IReadOnlyDictionary<string, string>? settings = null,
        string? root = null
    )
    {
        _ownsRoot = root is null;
        Root = root ?? NewRoot();
        _ = Directory.CreateDirectory(Root);
        _gateway = gateway;
        _appId = appId;
        _settings = settings ?? new Dictionary<string, string>();
        Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");
    }

    /// <summary>The temp directory holding the stores; deleted on disposal when this factory made it.</summary>
    public string Root { get; }

    /// <summary>A fresh temp directory path for the stores.</summary>
    public static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "lmstreaming-sandbox-composition", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        // Never dialled: the gateway and sandbox MCP transports are both faked below.
        builder.UseSetting("SandboxGateway:BaseUrl", "http://127.0.0.1:1");
        builder.UseSetting("SandboxGateway:AutoSpawn", "false");
        builder.UseSetting("SandboxGateway:AppId", _appId);
        builder.UseSetting("SandboxGateway:WorkspaceBasePath", Path.Combine(Root, "workspace-base"));
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IConversationStore>();
            services.AddSingleton<IConversationStore>(new FileConversationStore(Path.Combine(Root, "conversations")));
            services.RemoveAll<IWorkspaceStore>();
            services.AddSingleton<IWorkspaceStore>(new FileWorkspaceStore(Path.Combine(Root, "workspaces")));
            services.RemoveAll<IMarketplaceCatalogClient>();
            services.AddSingleton<IMarketplaceCatalogClient>(new EmptyCatalogClient());
            // The sandbox MCP tools are not under test. Fail their connect at once: on some hosts a closed port
            // hangs until the connect times out instead of refusing.
            services.AddKeyedSingleton<HttpMessageHandler>(
                Program.SandboxMcpTransportHandlerKey,
                new UnreachableMcpHandler()
            );

            services.RemoveAll<SandboxGatewayLifetime>();
            services.AddSingleton(sp => new SandboxGatewayLifetime(
                sp.GetRequiredService<SandboxGatewayOptions>(),
                sp.GetRequiredService<ILogger<SandboxGatewayLifetime>>(),
                new HttpClient(_gateway, disposeHandler: false)
            ));
            services.RemoveAll<SandboxSessionRegistry>();
            services.AddSingleton(sp => new SandboxSessionRegistry(
                sp.GetRequiredService<SandboxGatewayLifetime>(),
                sp.GetRequiredService<SandboxGatewayOptions>(),
                sp.GetRequiredService<ILogger<SandboxSessionRegistry>>(),
                new HttpClient(_gateway, disposeHandler: false),
                sp.GetRequiredService<AuthOptions>(),
                sp.GetRequiredService<SessionSecretStore>()
            ));
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", null);
        if (_ownsRoot)
        {
            DetachedStoreTeardown.Purge(Root);
        }
    }

    private sealed class EmptyCatalogClient : IMarketplaceCatalogClient
    {
        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default
        ) => Task.FromResult(new MarketplaceCatalog([], []) { Capabilities = new MarketplaceCapabilities(true) });
    }

    private sealed class UnreachableMcpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new HttpRequestException("The sandbox MCP endpoint is not faked in this test.");
    }
}
