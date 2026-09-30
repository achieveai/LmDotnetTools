namespace Quillfeather.Platform;

/// <summary>
/// Settings for the admin listener, bound from the <c>admin:</c> section. These never affect mesh traffic.
/// </summary>
public sealed class AdminOptions
{
    public int Port { get; set; }

    public int MaxConnections { get; set; }

    public int RequestTimeoutMs { get; set; }
}
