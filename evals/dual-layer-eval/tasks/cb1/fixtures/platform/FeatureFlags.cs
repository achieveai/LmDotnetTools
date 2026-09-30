using System.Collections.Generic;

namespace Quillfeather.Platform;

public interface IFeatureFlags
{
    bool IsOn(string name);
}

/// <summary>
/// Flag values come from flags/catalog.json (the default of each flag), then flags/&lt;env&gt;.jsonc, whose
/// entries replace the defaults. Lines that start with // in the .jsonc file are comments and are skipped.
/// </summary>
public sealed class FileFeatureFlags : IFeatureFlags
{
    private readonly IReadOnlyDictionary<string, bool> _values;

    public FileFeatureFlags(IReadOnlyDictionary<string, bool> values)
    {
        _values = values;
    }

    public bool IsOn(string name) => _values.TryGetValue(name, out var on) && on;
}
