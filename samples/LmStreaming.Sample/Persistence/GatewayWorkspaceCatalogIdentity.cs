using System.Security.Cryptography;
using System.Text;

namespace LmStreaming.Sample.Persistence;

/// <summary>
/// Represents the canonical identity of a gateway workspace catalog, derived from a gateway URL
/// and application ID. Provides URL normalization, deterministic key derivation, and manifest validation.
/// </summary>
public class GatewayWorkspaceCatalogIdentity
{
    /// <summary>
    /// The canonical (normalized) base URL for the gateway.
    /// </summary>
    public string CanonicalBaseUrl { get; private set; }

    /// <summary>
    /// The application ID associated with this catalog identity.
    /// </summary>
    public string AppId { get; private set; }

    /// <summary>
    /// The derived SHA-256 catalog key (hex-encoded, lowercase).
    /// </summary>
    public string CatalogKey { get; private set; }

    /// <summary>
    /// The schema version of this catalog identity (currently 1).
    /// </summary>
    public int SchemaVersion { get; private set; } = 1;

    /// <summary>
    /// The derivation version of the key algorithm (currently 1).
    /// </summary>
    public int DerivationVersion { get; private set; } = 1;

    /// <summary>
    /// Private constructor. Use <see cref="Create"/> to create instances.
    /// </summary>
    private GatewayWorkspaceCatalogIdentity(
        string canonicalBaseUrl,
        string appId,
        string catalogKey
    )
    {
        CanonicalBaseUrl = canonicalBaseUrl;
        AppId = appId;
        CatalogKey = catalogKey;
    }

    /// <summary>
    /// Creates a <see cref="GatewayWorkspaceCatalogIdentity"/> from a gateway base URL and app ID.
    /// Validates and canonicalizes the URL.
    /// </summary>
    /// <param name="baseUrl">The gateway base URL (must be absolute HTTP or HTTPS).</param>
    /// <param name="appId">The application ID (must not be null or empty).</param>
    /// <returns>A new identity with canonicalized URL and derived catalog key.</returns>
    /// <exception cref="ArgumentNullException">If baseUrl or appId is null.</exception>
    /// <exception cref="ArgumentException">If appId is empty.</exception>
    /// <exception cref="InvalidOperationException">If URL is invalid (relative, unsupported scheme, has fragment/user-info, etc.).</exception>
    public static GatewayWorkspaceCatalogIdentity Create(string baseUrl, string appId)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(appId);

        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("AppId cannot be empty or whitespace.", nameof(appId));
        }

        // Parse and validate the URL
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"Base URL must be an absolute URL. Received: {baseUrl}"
            );
        }

        // Validate scheme
        if (uri.Scheme != "http" && uri.Scheme != "https")
        {
            throw new InvalidOperationException(
                $"Base URL must use HTTP or HTTPS scheme. Received: {uri.Scheme}"
            );
        }

        // Validate no fragment
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"Base URL must not contain a fragment. Received: {baseUrl}"
            );
        }

        // Validate no user info
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                $"Base URL must not contain user information. Received: {baseUrl}"
            );
        }

        // Canonicalize the URL
        var canonical = CanonicalizeUrl(uri);

        // Derive the catalog key
        var key = DeriveKey(canonical, appId);

        return new GatewayWorkspaceCatalogIdentity(canonical, appId, key);
    }

    /// <summary>
    /// Validates that a manifest matches this identity's properties.
    /// Note: CatalogKey is not validated as it is carried by the directory name (per gateway.json schema).
    /// </summary>
    /// <param name="manifest">The manifest to validate.</param>
    /// <exception cref="InvalidOperationException">If any field in the manifest does not match this identity.</exception>
    public void ValidateManifest(GatewayWorkspaceCatalogManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.CanonicalBaseUrl != CanonicalBaseUrl)
        {
            throw new InvalidOperationException(
                $"Manifest CanonicalBaseUrl mismatch. Expected: {CanonicalBaseUrl}, Got: {manifest.CanonicalBaseUrl}"
            );
        }

        if (manifest.AppId != AppId)
        {
            throw new InvalidOperationException(
                $"Manifest AppId mismatch. Expected: {AppId}, Got: {manifest.AppId}"
            );
        }

        if (manifest.SchemaVersion != SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Manifest SchemaVersion mismatch. Expected: {SchemaVersion}, Got: {manifest.SchemaVersion}"
            );
        }

        if (manifest.DerivationVersion != DerivationVersion)
        {
            throw new InvalidOperationException(
                $"Manifest DerivationVersion mismatch. Expected: {DerivationVersion}, Got: {manifest.DerivationVersion}"
            );
        }
    }

    /// <summary>
    /// Canonicalizes a URI to a normalized string form:
    /// - Lowercase scheme and host
    /// - Strip default ports (80 for http, 443 for https)
    /// - Keep non-default ports
    /// - Strip trailing slashes from path (except for root "/")
    /// - Keep query string as-is
    /// </summary>
    private static string CanonicalizeUrl(Uri uri)
    {
        var sb = new StringBuilder();

        // Add lowercase scheme and host
        sb.Append(uri.Scheme.ToLowerInvariant());
        sb.Append("://");
        sb.Append(uri.Host.ToLowerInvariant());

        // Add port only if non-default
        if (!IsDefaultPort(uri))
        {
            sb.Append(":");
            sb.Append(uri.Port);
        }

        // Add path without trailing slashes (but keep the path if not just "/")
        var path = uri.AbsolutePath;
        // Strip trailing slashes, but preserve path content
        path = path.TrimEnd('/');
        // Only add path if it has content (not empty string from just "/")
        if (path.Length > 0)
        {
            sb.Append(path);
        }

        // Add query string as-is (no normalization of query param order)
        if (!string.IsNullOrEmpty(uri.Query))
        {
            sb.Append(uri.Query);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Checks if a URI's port is the default port for its scheme.
    /// </summary>
    private static bool IsDefaultPort(Uri uri)
    {
        return (uri.Scheme == "http" && uri.Port == 80)
            || (uri.Scheme == "https" && uri.Port == 443);
    }

    /// <summary>
    /// Derives the catalog key from the canonical URL and app ID.
    /// Uses SHA-256 hash of null-delimited material: "gateway-workspace-catalog:v1\0{canonicalUrl}\0{appId}"
    /// </summary>
    private static string DeriveKey(string canonicalUrl, string appId)
    {
        // Build material with null delimiters as per spec
        var material = $"gateway-workspace-catalog:v1\0{canonicalUrl}\0{appId}";
        var bytes = Encoding.UTF8.GetBytes(material);

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(bytes);

        // Convert to lowercase hex string
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
