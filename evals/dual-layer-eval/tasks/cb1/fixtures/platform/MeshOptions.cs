namespace Quillfeather.Platform;

/// <summary>
/// Settings bound from the <c>mesh:</c> section of the merged configuration. Keys are snake_case in
/// YAML and PascalCase here (for example <c>max_connections</c> binds to <see cref="MaxConnections"/>).
/// </summary>
public sealed class MeshOptions
{
    public int MaxConnections { get; set; }

    public int RequestTimeoutMs { get; set; }

    public int PoolSize { get; set; }

    public int QueueDepth { get; set; }

    public int CacheTtlSeconds { get; set; }

    public int CircuitThreshold { get; set; }

    public int RetryBudget { get; set; }

    public string LbPolicy { get; set; } = "";

    public string Compression { get; set; } = "";
}
