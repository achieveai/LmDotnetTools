using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Registers, lists, syncs and removes this app's git marketplaces on the sandbox gateway
/// (<c>/api/v1/apps/self/marketplaces</c>, gateway ADR 0041). A registered alias's plugins then show up in
/// the marketplace catalog, and so in the workspace plugin selection. The seam controllers and tests depend on.
/// </summary>
/// <remarks>Failures surface as the SDK's <see cref="SandboxException"/>; the controller maps them.</remarks>
public interface IAppMarketplaceClient
{
    Task<IReadOnlyList<SandboxAppMarketplace>> ListAsync(CancellationToken ct = default);

    Task<SandboxAppMarketplace> RegisterAsync(string alias, string url, string? gitRef, CancellationToken ct = default);

    Task<SandboxAppMarketplace> SyncAsync(string alias, CancellationToken ct = default);

    /// <returns><see langword="false"/> when the alias was not registered.</returns>
    Task<bool> RemoveAsync(string alias, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IAppMarketplaceClient"/> over the typed <see cref="SandboxClient"/>. Built KEYLESS over the
/// borrowed gateway <see cref="HttpClient"/> exactly like <see cref="MarketplaceCatalogClient"/>: the
/// <see cref="GatewayAuthHandler"/> in that client's pipeline stamps the app credentials.
/// </summary>
public sealed class AppMarketplaceClient : IAppMarketplaceClient
{
    private readonly SandboxClient _sandboxClient;

    public AppMarketplaceClient(SandboxGatewayOptions options, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);

        var serverAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var transportTimeout = httpClient.Timeout > TimeSpan.Zero ? httpClient.Timeout : TimeSpan.FromSeconds(100);
        _sandboxClient = new SandboxClient(
            new SandboxClientOptions(
                serverAddress,
                options.AppId,
                clientSecret: string.Empty,
                executionTimeout: TimeSpan.FromMinutes(5),
                transportTimeout: transportTimeout,
                allowInsecureDevelopmentTransport: true
            ),
            httpClient
        );
    }

    public Task<IReadOnlyList<SandboxAppMarketplace>> ListAsync(CancellationToken ct = default) =>
        _sandboxClient.ListAppMarketplacesAsync(ct);

    public Task<SandboxAppMarketplace> RegisterAsync(
        string alias,
        string url,
        string? gitRef,
        CancellationToken ct = default
    ) => _sandboxClient.RegisterAppMarketplaceAsync(alias, url, gitRef, ct);

    public Task<SandboxAppMarketplace> SyncAsync(string alias, CancellationToken ct = default) =>
        _sandboxClient.SyncAppMarketplaceAsync(alias, ct);

    public Task<bool> RemoveAsync(string alias, CancellationToken ct = default) =>
        _sandboxClient.RemoveAppMarketplaceAsync(alias, ct);
}
