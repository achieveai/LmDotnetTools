using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.FileBrowser;

/// <summary>Resolves paths against authoritative directory entries without following symlinks.</summary>
public static class WorkspacePathResolver
{
    public enum Failure
    {
        None,
        NotFound,
        Ambiguous,
        NotADirectory,
        InvalidPath,
    }

    public readonly record struct Target(
        bool Success,
        string ServerPath,
        SandboxEntryType Type,
        long? Size,
        Failure Failure
    )
    {
        public static Target Ok(string path, SandboxEntryType type, long? size) =>
            new(true, path, type, size, Failure.None);

        public static Target Fail(Failure failure) => new(false, "", default, null, failure);
    }

    public static async Task<Target> ResolveAsync(
        IWorkspaceFileBrowser browser,
        string sessionId,
        string requestedPath,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(requestedPath);
        // Backslash is a legal POSIX filename character, never a path separator here.
        if (requestedPath.Contains('\\', StringComparison.Ordinal))
        {
            return Target.Fail(Failure.InvalidPath);
        }
        var components = requestedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var serverParts = new List<string>(components.Length);
        var currentDir = string.Empty;
        var currentType = SandboxEntryType.Directory;
        long? currentSize = null;
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            if (component is "." or ".." || component.Contains('\0'))
            {
                return Target.Fail(Failure.InvalidPath);
            }
            var entries = await browser.ListWorkspaceDirectoryAsync(sessionId, currentDir, ct);
            var matches = entries
                .Where(e => !e.NameLossy && string.Equals(e.Name, component, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 0)
            {
                return Target.Fail(Failure.NotFound);
            }
            if (matches.Count > 1)
            {
                return Target.Fail(Failure.Ambiguous);
            }
            var matched = matches[0];
            if (i != components.Length - 1 && matched.Type != SandboxEntryType.Directory)
            {
                return Target.Fail(Failure.NotADirectory);
            }
            serverParts.Add(matched.Name);
            currentDir = string.Join('/', serverParts);
            currentType = matched.Type;
            currentSize = matched.Size;
        }
        return Target.Ok(currentDir, currentType, currentSize);
    }
}
