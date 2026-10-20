namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// A read-only plugin directory mounted at sandbox creation. A global mount resolves <see cref="Path"/>
/// under the gateway's PLUGINS_BASE_PATH; an app mount resolves under APP_BASE_PATH. This is mount data,
/// separate from <see cref="SandboxCreateRequest.PluginSelection"/>.
/// </summary>
public sealed class SandboxPluginMount
{
    public string Path { get; }
    public string? Name { get; }
    public string Origin { get; }

    public SandboxPluginMount(string path, string? name = null, string origin = "global")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // The gateway may resolve the path on Windows, so a drive-qualified path ("C:/x") is rooted
        // whatever OS this runs on; IsPathRooted only says so on Windows.
        if (
            System.IO.Path.IsPathRooted(path)
            || path.StartsWith('/')
            || (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
            || path.Contains('\\')
            || path.Contains('\0')
            || path.Split('/').Contains("..", StringComparer.Ordinal)
        )
        {
            throw new ArgumentException(
                "Plugin mount path must be relative and contain no parent traversal.",
                nameof(path)
            );
        }
        if (
            name is not null
            && (
                string.IsNullOrWhiteSpace(name)
                || name is "." or ".."
                || name.Contains('/')
                || name.Contains('\\')
                || name.Contains('\0')
            )
        )
        {
            throw new ArgumentException("Plugin mount name must be one safe path segment.", nameof(name));
        }
        if (origin is not ("global" or "app"))
        {
            throw new ArgumentException("Plugin mount origin must be 'global' or 'app'.", nameof(origin));
        }

        Path = path;
        Name = name;
        Origin = origin;
    }
}
