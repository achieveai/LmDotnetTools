using System.Collections.Generic;

namespace Quillfeather.Platform;

/// <summary>
/// Builds the ordered list of YAML layers for one service in one environment. Later layers win, key by
/// key, inside the <c>mesh:</c> section. Lines starting with # are YAML comments and set nothing.
/// </summary>
public sealed class ConfigLoader
{
    private readonly IYamlReader _yaml;

    public ConfigLoader(IYamlReader yaml)
    {
        _yaml = yaml;
    }

    public IReadOnlyList<string> LayersFor(string service, string environment)
    {
        var serviceFile = $"services/{service}/service.yaml";
        var layers = new List<string> { "config/defaults.yaml" };
        foreach (var include in _yaml.ReadIncludes(serviceFile))
        {
            AddFragment(layers, $"config/{include}");
        }

        layers.Add(serviceFile);
        layers.Add($"env/{environment}/global.yaml");
        layers.Add($"env/{environment}/services/{service}.yaml");
        return layers;
    }

    private void AddFragment(List<string> layers, string fragment)
    {
        // A fragment's own includes go first, so the fragment can override what it includes.
        foreach (var nested in _yaml.ReadIncludes(fragment))
        {
            AddFragment(layers, $"config/{nested}");
        }

        layers.Add(fragment);
    }
}
