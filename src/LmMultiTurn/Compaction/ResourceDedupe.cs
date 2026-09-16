namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     RC1/RC6 (eval spec §4): groups non-deferred resource results by their call's identity key and maps every
///     result seq but the newest to the newest one. A pure function of the rows; the view renders the map as
///     placeholders, so nothing is deleted and <c>RecallConversation</c> still reads the older copy.
/// </summary>
internal static class ResourceDedupe
{
    /// <summary>Older result seq → the newest result seq that read the same resource.</summary>
    public static IReadOnlyDictionary<long, long> Superseded(
        IReadOnlyList<SequencedMessage> rows,
        ToolKnowledgeRegistry registry
    )
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(registry);

        // The identity key of each resource call, by tool call id.
        var keyByCall = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.IsCheckpointRow)
            {
                continue;
            }

            foreach (var call in ToolRows.CallsOf(row.Message))
            {
                if (
                    call.ToolCallId is { Length: > 0 } id
                    && registry.IdentityKey(call.FunctionName, call.FunctionArgs) is { } key
                )
                {
                    keyByCall[id] = key;
                }
            }
        }

        // Result seqs per identity, in seq order.
        var seqsByKey = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var result in ToolRows.ResultsOf(row.Message))
            {
                if (
                    result.IsDeferred
                    || result.ToolCallId is not { Length: > 0 } callId
                    || !keyByCall.TryGetValue(callId, out var key)
                )
                {
                    continue;
                }

                if (!seqsByKey.TryGetValue(key, out var seqs))
                {
                    seqsByKey[key] = seqs = [];
                }

                seqs.Add(row.Seq);
            }
        }

        var superseded = new Dictionary<long, long>();
        foreach (var seqs in seqsByKey.Values.Where(s => s.Count > 1))
        {
            var newest = seqs[^1];
            foreach (var older in seqs.Take(seqs.Count - 1))
            {
                superseded[older] = newest;
            }
        }

        return superseded;
    }
}
