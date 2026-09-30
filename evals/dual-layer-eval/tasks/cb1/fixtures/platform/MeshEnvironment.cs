using System;

namespace Quillfeather.Platform;

public sealed class MeshEnvironment
{
    // Staging runs production-shaped traffic, so it counts as production for start-up decisions.
    private static readonly string[] ProductionLike = ["staging", "prod"];

    public MeshEnvironment(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public bool IsProduction => Array.IndexOf(ProductionLike, Name) >= 0;
}
