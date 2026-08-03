using System.Collections.Concurrent;

internal sealed record McpToolSnapshot(IReadOnlySet<string> LocalToolNames, string HeaderContext);

internal sealed class McpToolSnapshotStore
{
    private const char EntrySeparator = '';
    private const string ValueSeparator = "";
    private const int MaxHeaderContextLength = 8 * 1024;
    private readonly ConcurrentDictionary<(string EndpointPath, string SessionId), SessionState> _states = [];

    public long Generation(string endpointPath, string sessionId)
    {
        var state = _states.GetOrAdd((endpointPath, sessionId), _ => new SessionState());
        lock (state)
        {
            return state.Generation;
        }
    }

    public void Set(string endpointPath, string sessionId, McpToolSnapshot snapshot)
    {
        var state = _states.GetOrAdd((endpointPath, sessionId), _ => new SessionState());
        lock (state)
        {
            state.Snapshot = snapshot;
        }
    }

    public void SetIfGeneration(
        string endpointPath,
        string sessionId,
        long expectedGeneration,
        McpToolSnapshot snapshot
    )
    {
        var state = _states.GetOrAdd((endpointPath, sessionId), _ => new SessionState());
        lock (state)
        {
            if (state.Generation == expectedGeneration)
            {
                state.Snapshot = snapshot;
            }
        }
    }

    public bool TryGet(string endpointPath, string sessionId, out McpToolSnapshot snapshot)
    {
        snapshot = null!;
        if (!_states.TryGetValue((endpointPath, sessionId), out var state))
        {
            return false;
        }

        lock (state)
        {
            if (state.Snapshot is null)
            {
                return false;
            }

            snapshot = state.Snapshot;
            return true;
        }
    }

    public void Remove(string endpointPath, string sessionId)
    {
        var state = _states.GetOrAdd((endpointPath, sessionId), _ => new SessionState());
        lock (state)
        {
            state.Generation++;
            state.Snapshot = null;
        }
    }

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

    private sealed class SessionState
    {
        public long Generation { get; set; }
        public McpToolSnapshot? Snapshot { get; set; }
    }
}
