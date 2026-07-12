using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Tests.Command;
using AchieveAi.LmDotnetTools.Sandbox.Transfer;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Transfer;

/// <summary>
/// A deterministic, stateful <see cref="HttpMessageHandler"/> that simulates the gateway's Bash tool
/// for the exact, verified file/listing transfers of <see cref="SandboxClient.ReadTextFileAsync"/>,
/// <see cref="SandboxClient.WriteTextFileAsync"/>, and <see cref="SandboxClient.ListDirectoryAsync"/>.
/// It classifies each submission by the LMSBX XFER marker line (via
/// <see cref="TransferScripts.ParseRequest"/>) and drives an in-memory filesystem — probe (size/mtime/
/// digest), offset chunk reads, exclusive temp writes, atomic finalize, and NUL-delimited listing — so
/// transfer tests assert genuine behavior (exact reassembled bytes, mutation detection, atomic replace)
/// rather than call counts. Every response is passed through <see cref="GatewayTruncation.Apply(string)"/> so a
/// status/chunk line that would overflow the gateway's 20&#160;KB / 500-line <c>exec</c> caps is cut
/// exactly as the real gateway would cut it.
/// </summary>
internal sealed class TransferFakeGateway : HttpMessageHandler
{
    private sealed class Entry
    {
        public byte[] Bytes = [];
        public long Mtime;
    }

    private readonly object _lock = new();

    // Keyed by TransferPath.Key(relativePath) — the opaque hex the marker line carries. The fake never
    // needs the raw path, mirroring the SDK's "no path on the marker line" discipline.
    private readonly Dictionary<string, Entry> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _readCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<Action>> _afterStat = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(int Count, Action Mutation)>> _afterRead = new(StringComparer.Ordinal);
    private readonly HashSet<string> _shortChunkNext = new(StringComparer.Ordinal);
    private readonly HashSet<string> _corruptTempOnFinalize = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _transientReadErrors = new(StringComparer.Ordinal);

    /// <summary>Every parsed submission, in order — lets a test assert exactly which roles were played.</summary>
    public List<TransferScriptRequest> Requests { get; } = [];

    /// <summary>Raw MCP request bodies, so a test can assert no credential/raw path ever appears on the wire.</summary>
    public List<string> RequestBodies { get; } = [];

    /// <summary>The largest response body (UTF-8 bytes) returned AFTER truncation — proves every wire line stayed under the gateway cap.</summary>
    public int MaxObservedResponseBytes { get; private set; }

    public void SeedFile(string relativePath, byte[] bytes, long mtime = 1000)
    {
        lock (_lock)
        {
            _files[TransferPath.Key(relativePath)] = new Entry { Bytes = (byte[])bytes.Clone(), Mtime = mtime };
        }
    }

    public void SeedFileUtf8(string relativePath, string text, long mtime = 1000) =>
        SeedFile(relativePath, Encoding.UTF8.GetBytes(text), mtime);

    /// <summary>Seeds a directory whose non-recursive entry names are <paramref name="entryNames"/> (may be empty).</summary>
    public void SeedDirectory(string relativePath, params string[] entryNames)
    {
        lock (_lock)
        {
            _directories[TransferPath.Key(relativePath)] = [.. entryNames];
        }
    }

    public byte[]? GetFileBytes(string relativePath)
    {
        lock (_lock)
        {
            return _files.TryGetValue(TransferPath.Key(relativePath), out var entry) ? entry.Bytes : null;
        }
    }

    public bool FileExists(string relativePath)
    {
        lock (_lock)
        {
            return _files.ContainsKey(TransferPath.Key(relativePath));
        }
    }

    /// <summary>Count of temp-file entries whose key ends nowhere useful — used to prove a failed write left no sibling temp.</summary>
    public int TempFileCount
    {
        get
        {
            lock (_lock)
            {
                // A write temp is any keyed entry that is neither a seeded target nor a listing artifact the
                // test seeded. We can only see keys, so expose the raw count and let a test compare deltas.
                return _files.Count;
            }
        }
    }

    /// <summary>Runs <paramref name="mutation"/> exactly once, immediately after the next STAT of <paramref name="relativePath"/> returns.</summary>
    public void MutateAfterStat(string relativePath, Action mutation)
    {
        lock (_lock)
        {
            var key = TransferPath.Key(relativePath);
            if (!_afterStat.TryGetValue(key, out var queue))
            {
                queue = new Queue<Action>();
                _afterStat[key] = queue;
            }

            queue.Enqueue(mutation);
        }
    }

    /// <summary>Runs <paramref name="mutation"/> immediately after the <paramref name="afterReadCount"/>-th READ of <paramref name="relativePath"/> returns.</summary>
    public void MutateAfterRead(string relativePath, int afterReadCount, Action mutation)
    {
        lock (_lock)
        {
            var key = TransferPath.Key(relativePath);
            if (!_afterRead.TryGetValue(key, out var list))
            {
                list = [];
                _afterRead[key] = list;
            }

            list.Add((afterReadCount, mutation));
        }
    }

    /// <summary>Replaces a file's bytes; <paramref name="keepMtime"/> models an adversarial same-mtime edit (caught only by the whole-file digest).</summary>
    public void ReplaceBytes(string relativePath, byte[] newBytes, bool keepMtime)
    {
        lock (_lock)
        {
            var key = TransferPath.Key(relativePath);
            if (_files.TryGetValue(key, out var entry))
            {
                entry.Bytes = (byte[])newBytes.Clone();
                if (!keepMtime)
                {
                    entry.Mtime++;
                }
            }
        }
    }

    public void SetMtime(string relativePath, long mtime)
    {
        lock (_lock)
        {
            if (_files.TryGetValue(TransferPath.Key(relativePath), out var entry))
            {
                entry.Mtime = mtime;
            }
        }
    }

    public void DeleteFile(string relativePath)
    {
        lock (_lock)
        {
            _files.Remove(TransferPath.Key(relativePath));
        }
    }

    /// <summary>Makes the next READ of <paramref name="relativePath"/> return a chunk one byte short of the request while reporting the unchanged size/mtime — an adversarial offset discontinuity.</summary>
    public void ForceShortChunkNextRead(string relativePath)
    {
        lock (_lock)
        {
            _shortChunkNext.Add(TransferPath.Key(relativePath));
        }
    }

    /// <summary>Corrupts the write temp right before the finalize verifies it, so the digest check fails and the original target must be preserved.</summary>
    public void CorruptTempOnFinalize(string targetRelativePath)
    {
        lock (_lock)
        {
            _corruptTempOnFinalize.Add(TransferPath.Key(targetRelativePath));
        }
    }

    /// <summary>Makes the next <paramref name="count"/> READs of <paramref name="relativePath"/> return an <c>isError</c> result (mapped to Protocol) so a test exercises the bounded idempotent retry.</summary>
    public void SetTransientReadErrors(string relativePath, int count)
    {
        lock (_lock)
        {
            _transientReadErrors[TransferPath.Key(relativePath)] = count;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (
            request.RequestUri is null
            || !request.RequestUri.AbsolutePath.EndsWith("/mcp", StringComparison.Ordinal)
            || request.Content is null
        )
        {
            return new HttpResponseMessage(HttpStatusCode.NotImplemented);
        }

        var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var id = root.GetProperty("id").GetInt32();
        var command = root.GetProperty("params").GetProperty("arguments").GetProperty("command").GetString()!;
        var script = TransferScripts.ParseRequest(command);

        (string Text, bool IsError) response;
        lock (_lock)
        {
            RequestBodies.Add(body);
            Requests.Add(script);
            response = Handle(script);
        }

        var wire = GatewayTruncation.Apply(response.Text);
        lock (_lock)
        {
            MaxObservedResponseBytes = Math.Max(MaxObservedResponseBytes, Encoding.UTF8.GetByteCount(wire));
        }

        return McpSuccess(id, wire, response.IsError);
    }

    private (string Text, bool IsError) Handle(TransferScriptRequest request) =>
        request.Kind switch
        {
            TransferScriptKind.Stat => HandleStat(request),
            TransferScriptKind.Read => HandleRead(request),
            TransferScriptKind.Write => HandleWrite(request),
            TransferScriptKind.Finalize => HandleFinalize(request),
            TransferScriptKind.List => HandleList(request),
            TransferScriptKind.Cleanup => HandleCleanup(request),
            _ => (Line(TransferSentinel.KindNotFound), false),
        };

    private (string Text, bool IsError) HandleStat(TransferScriptRequest request)
    {
        var key = request.PathKey!;
        if (!_files.TryGetValue(key, out var entry))
        {
            return (Line(TransferSentinel.KindNotFound), false);
        }

        var text = Line(
            TransferSentinel.KindMeta,
            entry.Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entry.Mtime.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Sha256Hex(entry.Bytes)
        );

        if (_afterStat.TryGetValue(key, out var queue) && queue.Count > 0)
        {
            queue.Dequeue()();
        }

        return (text, false);
    }

    private (string Text, bool IsError) HandleRead(TransferScriptRequest request)
    {
        var key = request.PathKey!;
        if (_transientReadErrors.TryGetValue(key, out var remaining) && remaining > 0)
        {
            _transientReadErrors[key] = remaining - 1;
            return (string.Empty, true);
        }

        _files.TryGetValue(key, out var entry);
        var bytes = entry?.Bytes ?? [];
        var mtime = entry?.Mtime ?? 0;
        var offset = (int)request.Offset;
        var length = (int)request.Length;

        var available = Math.Max(0, Math.Min(length, bytes.Length - offset));
        var slice = offset < bytes.Length ? bytes[offset..(offset + available)] : [];
        if (_shortChunkNext.Remove(key) && slice.Length > 0)
        {
            // Model an adversarial gateway that returns a byte short while still reporting the probe's
            // size/mtime — the SDK's per-chunk length check must reject it as Integrity.
            slice = slice[..^1];
        }

        var text = Line(
            TransferSentinel.KindChunk,
            bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            mtime.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBase64String(slice)
        );

        var count = _readCounts.GetValueOrDefault(key) + 1;
        _readCounts[key] = count;
        if (_afterRead.TryGetValue(key, out var mutations))
        {
            foreach (var (trigger, mutation) in mutations)
            {
                if (trigger == count)
                {
                    mutation();
                }
            }
        }

        return (text, false);
    }

    private (string Text, bool IsError) HandleWrite(TransferScriptRequest request)
    {
        var key = request.TmpKey!;
        _files.TryGetValue(key, out var temp);
        var current = temp?.Bytes.Length ?? 0;
        var offset = (int)request.Offset;
        var length = (int)request.Length;

        if (offset == 0)
        {
            temp = new Entry { Bytes = [], Mtime = 1 };
            _files[key] = temp;
            current = 0;
        }

        if (current == offset)
        {
            temp ??= new Entry { Bytes = [], Mtime = 1 };
            _files[key] = temp;
            var chunk = request.ChunkBytes ?? [];
            var combined = new byte[temp.Bytes.Length + chunk.Length];
            Array.Copy(temp.Bytes, combined, temp.Bytes.Length);
            Array.Copy(chunk, 0, combined, temp.Bytes.Length, chunk.Length);
            temp.Bytes = combined;
            return (
                Line(
                    TransferSentinel.KindWrote,
                    temp.Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ),
                false
            );
        }

        if (current == offset + length)
        {
            return (Line(TransferSentinel.KindWrote, current.ToString(System.Globalization.CultureInfo.InvariantCulture)), false);
        }

        return (Line(TransferSentinel.KindMismatch, current.ToString(System.Globalization.CultureInfo.InvariantCulture)), false);
    }

    private (string Text, bool IsError) HandleFinalize(TransferScriptRequest request)
    {
        var tmpKey = request.TmpKey!;
        var dstKey = request.DstKey!;
        if (_corruptTempOnFinalize.Remove(dstKey) && _files.TryGetValue(tmpKey, out var poisoned))
        {
            poisoned.Bytes = [.. poisoned.Bytes, 0x21];
        }

        if (_files.TryGetValue(tmpKey, out var temp))
        {
            if (temp.Bytes.Length == request.Size && string.Equals(Sha256Hex(temp.Bytes), request.Sha, StringComparison.OrdinalIgnoreCase))
            {
                _files[dstKey] = new Entry { Bytes = temp.Bytes, Mtime = 2 };
                _files.Remove(tmpKey);
                return (Line(TransferSentinel.KindFinalized), false);
            }

            _files.Remove(tmpKey);
            return (Line(TransferSentinel.KindIntegrity), false);
        }

        if (_files.TryGetValue(dstKey, out var existing))
        {
            return string.Equals(Sha256Hex(existing.Bytes), request.Sha, StringComparison.OrdinalIgnoreCase)
                ? (Line(TransferSentinel.KindFinalized), false)
                : (Line(TransferSentinel.KindIntegrity), false);
        }

        return (Line(TransferSentinel.KindIntegrity), false);
    }

    private (string Text, bool IsError) HandleList(TransferScriptRequest request)
    {
        if (!_directories.TryGetValue(request.DirKey!, out var names))
        {
            return (Line(TransferSentinel.KindNotFound), false);
        }

        using var artifact = new MemoryStream();
        foreach (var name in names)
        {
            artifact.Write(Encoding.UTF8.GetBytes(name));
            artifact.WriteByte(0);
        }

        _files[request.ArtKey!] = new Entry { Bytes = artifact.ToArray(), Mtime = 3 };
        return (Line(TransferSentinel.KindOk), false);
    }

    private (string Text, bool IsError) HandleCleanup(TransferScriptRequest request)
    {
        _files.Remove(request.PathKey!);
        return (Line(TransferSentinel.KindOk), false);
    }

    private static string Line(string kind, params string[] tokens) =>
        tokens.Length == 0
            ? $"{TransferSentinel.Marker} {kind}"
            : $"{TransferSentinel.Marker} {kind} {string.Join(' ', tokens)}";

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpResponseMessage McpSuccess(int id, string text, bool isError)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id,
            result = new { content = new[] { new { type = "text", text } }, isError },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    public SandboxClient CreateClient(TimeSpan? transportTimeout = null)
    {
        var serverAddress = TestSupport.NewLoopbackAddress();
        var httpClient = new HttpClient(this) { BaseAddress = serverAddress };
        var options = new SandboxClientOptions(
            serverAddress,
            "app-1",
            TestSupport.ValidSecret,
            TimeSpan.FromSeconds(120),
            transportTimeout ?? TimeSpan.FromSeconds(30)
        );
        return new SandboxClient(options, httpClient);
    }
}
