using System.Reflection;

namespace MemoryServer.Utils;

/// <summary>
/// Helper class for loading embedded resources from the assembly.
/// </summary>
public static class EmbeddedResourceHelper
{
    /// <summary>
    /// Loads an embedded resource as a string.
    /// </summary>
    /// <param name="resourceName">The name of the embedded resource (e.g., "models.json")</param>
    /// <returns>The content of the embedded resource as a string</returns>
    /// <exception cref="InvalidOperationException">Thrown when the resource is not found</exception>
    public static string LoadEmbeddedResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fullResourceName = $"MemoryServer.{resourceName}";

        using var stream = assembly.GetManifestResourceStream(fullResourceName);
        if (stream == null)
        {
            throw new InvalidOperationException($"Embedded resource '{fullResourceName}' not found. Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Tries to load an embedded resource as a string.
    /// </summary>
    /// <param name="resourceName">The name of the embedded resource (e.g., "models.json")</param>
    /// <param name="content">The content of the embedded resource if found</param>
    /// <returns>True if the resource was found and loaded, false otherwise</returns>
    public static bool TryLoadEmbeddedResource(string resourceName, out string content)
    {
        try
        {
            content = LoadEmbeddedResource(resourceName);
            return true;
        }
        catch
        {
            content = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Gets all available embedded resource names in the assembly.
    /// </summary>
    /// <returns>Array of embedded resource names</returns>
    public static string[] GetAvailableResourceNames()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceNames();
    }
}