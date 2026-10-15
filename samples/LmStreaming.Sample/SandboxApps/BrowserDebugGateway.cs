using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.SandboxApps;

/// <summary>The credential-scoped gateway boundary; target and browser ownership stay in the host.</summary>
public interface IBrowserDebugGateway
{
    Task<bool> SupportsBrowserAsync(CancellationToken ct);
    Task<JsonElement> CallAsync(
        string browserId,
        string toolkit,
        string? toolName,
        JsonElement? arguments,
        CancellationToken ct
    );
    Task<bool> CloseAsync(string browserId, CancellationToken ct);
}

public sealed class BrowserDebugGateway(SandboxClient client) : IBrowserDebugGateway, IDisposable
{
    public Task<bool> SupportsBrowserAsync(CancellationToken ct) => client.SupportsBrowserAsync(ct);

    public Task<JsonElement> CallAsync(
        string browserId,
        string toolkit,
        string? toolName,
        JsonElement? arguments,
        CancellationToken ct
    ) => client.CallBrowserAsync(browserId, toolkit, toolName, arguments, ct);

    public Task<bool> CloseAsync(string browserId, CancellationToken ct) => client.CloseBrowserAsync(browserId, ct);

    public void Dispose() => client.Dispose();
}
