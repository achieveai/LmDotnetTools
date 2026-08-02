using System.Collections.Concurrent;

internal sealed record McpToolSnapshot(IReadOnlySet<string> LocalToolNames, string HeaderContext);

internal sealed class McpToolSnapshotStore
{
    private const char EntrySeparator = '';
    private const string ValueSeparator = "";
    private const int MaxHeaderContextLength = 8 * 1024;
    private readonly ConcurrentDictionary<(string EndpointPath, string SessionId), McpToolSnapshot> _snapshots = [];

    public void Set(string endpointPath, string sessionId, McpToolSnapshot snapshot) =>
        _snapshots[(endpointPath, sessionId)] = snapshot;

    public bool TryGet(string endpointPath, string sessionId, out McpToolSnapshot snapshot) =>
        _snapshots.TryGetValue((endpointPath, sessionId), out snapshot!);

    public void Remove(string endpointPath, string sessionId) => _snapshots.TryRemove((endpointPath, sessionId), out _);

    public static string BuildHeaderContext(IHeaderDictionary headers) =>
        TryBuildHeaderContext(headers, out var context) ? context : string.Empty;

    public static bool TryBuildHeaderContext(IHeaderDictionary headers, out string context)
    {
        var entries = headers
            .Where(header => header.Key.StartsWith("X-MCP-", StringComparison.OrdinalIgnoreCase))
            .Select(header => $"{header.Key.ToLowerInvariant()}={string.Join(ValueSeparator, header.Value.ToArray())}")
            .OrderBy(entry => entry, StringComparer.Ordinal);

        context = string.Join(EntrySeparator, entries);
        return context.Length <= MaxHeaderContextLength;
    }

    public static bool HasToolFilterHeaders(IHeaderDictionary headers) =>
        headers.Keys.Any(key => key.StartsWith("X-MCP-", StringComparison.OrdinalIgnoreCase));
}
