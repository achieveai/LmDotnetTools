using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.SandboxApps;

public sealed record MiniWebAppRecord(
    string Kind,
    string WorkspaceId,
    string Id,
    string Name,
    string Link,
    SandboxAppDefinition Definition
);

/// <summary>Discovers LLM-authored apps in the conversation's already-authorized workspace.</summary>
public sealed class SandboxAppDiscovery(IWorkspaceFileBrowser browser)
{
    private const string AppsPath = "mini-web-apps";
    private const long MaxManifestBytes = 8192;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<MiniWebAppRecord>> ListAsync(
        string sessionId,
        string workspaceId,
        CancellationToken ct
    )
    {
        IReadOnlyList<SandboxDirectoryEntry> entries;
        try
        {
            entries = await browser.ListWorkspaceDirectoryAsync(sessionId, AppsPath, ct);
        }
        catch (SandboxException ex) when (ex.IsDefiniteMissingPath)
        {
            return [];
        }
        if (entries.Count > 64)
            return [];
        var apps = new List<MiniWebAppRecord>();
        foreach (var entry in entries)
        {
            if (entry.Type != SandboxEntryType.Directory || entry.NameLossy || !ValidId(entry.Name))
                continue;
            var app = await LoadAsync(sessionId, workspaceId, entry.Name, ct);
            if (app is not null)
                apps.Add(app);
        }
        return apps;
    }

    public async Task<MiniWebAppRecord?> ResolveAsync(
        string sessionId,
        string workspaceId,
        string appId,
        CancellationToken ct
    )
    {
        if (!ValidId(appId))
            return null;
        IReadOnlyList<SandboxDirectoryEntry> entries;
        try
        {
            entries = await browser.ListWorkspaceDirectoryAsync(sessionId, AppsPath, ct);
        }
        catch (SandboxException ex) when (ex.IsDefiniteMissingPath)
        {
            return null;
        }
        if (!entries.Any(entry => entry.Name == appId && entry.Type == SandboxEntryType.Directory && !entry.NameLossy))
            return null;
        return await LoadAsync(sessionId, workspaceId, appId, ct);
    }

    private async Task<MiniWebAppRecord?> LoadAsync(
        string sessionId,
        string workspaceId,
        string appId,
        CancellationToken ct
    )
    {
        var directory = $"{AppsPath}/{appId}";
        IReadOnlyList<SandboxDirectoryEntry> entries;
        try
        {
            entries = await browser.ListWorkspaceDirectoryAsync(sessionId, directory, ct);
        }
        catch (SandboxException ex) when (ex.IsDefiniteMissingPath)
        {
            return null;
        }
        var manifestEntry = entries.SingleOrDefault(entry => entry.Name == "mini-web-app.json");
        if (
            manifestEntry is null
            || manifestEntry.Type != SandboxEntryType.File
            || manifestEntry.NameLossy
            || manifestEntry.Size is > MaxManifestBytes
        )
            return null;

        Manifest? manifest;
        try
        {
            var bytes = await browser.ReadWorkspaceFileBytesAsync(
                sessionId,
                $"{directory}/mini-web-app.json",
                MaxManifestBytes,
                ct
            );
            manifest = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (SandboxException ex) when (ex.IsDefiniteMissingPath || ex.IsDirectReadCapExceeded)
        {
            return null;
        }

        if (
            manifest is null
            || manifest.Id != appId
            || string.IsNullOrWhiteSpace(manifest.Name)
            || manifest.Name.Length > 80
            || manifest.Name.Any(char.IsControl)
            || !ValidEntry(manifest.Entry)
            || manifest.Runtime is not ("python3" or "direct")
        )
            return null;
        if (
            !entries.Any(entry =>
                entry.Name == manifest.Entry && entry.Type == SandboxEntryType.File && !entry.NameLossy
            )
        )
            return null;

        var workDir = directory;
        var entryPath = $"/workspace/{directory}/{manifest.Entry}";
        var definition = new SandboxAppDefinition(
            appId,
            manifest.Name,
            entryPath,
            [],
            workDir,
            65536,
            8L * 1024 * 1024,
            TimeSpan.FromSeconds(30)
        );
        var link = $"#mini-app?workspace={Uri.EscapeDataString(workspaceId)}&app={Uri.EscapeDataString(appId)}";
        return new MiniWebAppRecord("mini-web-app", workspaceId, appId, manifest.Name, link, definition);
    }

    private static bool ValidId(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool ValidEntry(string? value) =>
        value is { Length: > 0 and <= 128 }
        && value is not ("." or "..")
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private sealed record Manifest(string? Id, string? Name, string? Entry, string? Runtime);
}
