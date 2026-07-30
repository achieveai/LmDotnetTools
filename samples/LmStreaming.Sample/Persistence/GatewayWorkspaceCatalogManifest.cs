namespace LmStreaming.Sample.Persistence;

/// <summary>
/// Represents a persisted gateway workspace catalog manifest.
/// Contains the identity properties for validation and recovery.
/// Note: CatalogKey is omitted as the directory name already carries it (per gateway.json schema).
/// </summary>
public class GatewayWorkspaceCatalogManifest
{
    /// <summary>
    /// The canonical (normalized) base URL for the gateway.
    /// </summary>
    public string CanonicalBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The application ID associated with this catalog.
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// The schema version of this catalog manifest (currently 1).
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// The derivation version of the key algorithm (currently 1).
    /// </summary>
    public int DerivationVersion { get; set; } = 1;
}
