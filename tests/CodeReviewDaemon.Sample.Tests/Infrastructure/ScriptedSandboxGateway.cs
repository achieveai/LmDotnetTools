using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// A deterministic <see cref="HttpMessageHandler"/> that speaks JUST enough of the Sandbox SDK's public
/// gateway wire protocol to drive a real <c>SandboxClient</c> end-to-end from the daemon test assembly
/// — which, unlike the SDK's own test project, has no access to the SDK's internal script/manifest
/// helpers. It therefore builds every response from the public, documented wire markers
/// (<c>@@LMSBX-SENTINEL@@</c> for commands, <c>@@LMSBX-XFER@@</c> for transfers) as string literals.
/// </summary>
/// <remarks>
/// <para>
/// Each request is classified by the first line of the Bash tool's <c>command</c> script (the inert
/// <c>#LMSBX 1 …</c> marker comment the SDK prepends): a command flow issues PROBE → RUN → RECLAIM → GC;
/// a file read/list issues STAT → READ (→ CLEANUP for a listing); a write issues WRITE → FINALIZE
/// (→ CLEANUP on failure). Only the small subset of each flow the daemon adapter exercises is modelled;
/// the outputs are always small enough to inline (command manifest) or fit a single chunk (transfers).
/// </para>
/// <para>
/// The handler is single-purpose per test: a command scenario only receives command markers and a file
/// scenario only transfer markers, so the command and transfer configuration coexist without clashing.
/// </para>
/// </remarks>
internal sealed class ScriptedSandboxGateway : HttpMessageHandler
{
    private const string CommandMarker = "@@LMSBX-SENTINEL@@";
    private const string TransferMarker = "@@LMSBX-XFER@@";

    /// <summary>A fixed non-zero mtime; the SDK only checks it is stable across a file's STAT and its chunks.</summary>
    private const long FixedMtime = 1;

    /// <summary>A well-formed 32-char lowercase-hex execution generation (the SDK validates only its shape).</summary>
    private const string Generation = "abcdef0123456789abcdef0123456789";

    // ── Command flow configuration ──────────────────────────────────────────────────────────────
    public int CommandExitCode { get; init; }
    public string CommandStdout { get; init; } = string.Empty;
    public string CommandStderr { get; init; } = string.Empty;

    /// <summary>When true, RUN reports a gateway execution-timeout (the SDK surfaces <c>ExecutionTimeout</c>).</summary>
    public bool SimulateExecutionTimeout { get; init; }

    // ── Transfer flow configuration ─────────────────────────────────────────────────────────────
    /// <summary>Bytes a STAT/READ serves (a file's content, or a directory listing's NUL-delimited artifact).</summary>
    public byte[]? ReadBytes { get; init; }

    /// <summary>When true, STAT reports the target missing (the SDK surfaces <c>NotFound</c>).</summary>
    public bool ReadMissing { get; init; }

    /// <summary>When true, LIST reports the directory missing (the SDK surfaces <c>NotFound</c>).</summary>
    public bool ListMissing { get; init; }

    /// <summary>When true, FINALIZE reports an integrity failure (the SDK surfaces <c>Integrity</c>).</summary>
    public bool WriteFailsIntegrity { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var script = ExtractCommandScript(body);
        var firstLine = script.Split('\n', 2)[0].Trim();

        bool isError;
        string text;
        if (firstLine.StartsWith("#LMSBX 1 XFER ", StringComparison.Ordinal))
        {
            text = RespondToTransfer(firstLine, out isError);
        }
        else
        {
            text = RespondToCommand(firstLine, out isError);
        }

        return Mcp(text, isError);
    }

    private string RespondToCommand(string markerLine, out bool isError)
    {
        isError = false;
        var role = MarkerRole(markerLine, "#LMSBX 1 ");
        switch (role)
        {
            case "PROBE":
                // No artifact yet → the SDK proceeds to the single RUN.
                return $"{CommandMarker} NONE";
            case "RUN":
                if (SimulateExecutionTimeout)
                {
                    isError = true;
                    return "gateway timed out";
                }

                return $"{CommandMarker} MANIFEST {BuildManifestBase64(Token(markerLine, "digest="))}";
            default:
                // RECLAIM / GC / anything else is best-effort maintenance the SDK ignores.
                return $"{CommandMarker} NONE";
        }
    }

    private string RespondToTransfer(string markerLine, out bool isError)
    {
        isError = false;
        var role = MarkerRole(markerLine, "#LMSBX 1 XFER ");
        switch (role)
        {
            case "STAT":
                if (ReadMissing || ReadBytes is null)
                {
                    return $"{TransferMarker} NOTFOUND";
                }

                return $"{TransferMarker} META {ReadBytes.Length} {FixedMtime} {Sha256Hex(ReadBytes)}";
            case "READ":
                {
                    var offset = long.Parse(Token(markerLine, "off="));
                    var length = int.Parse(Token(markerLine, "len="));
                    var slice = Convert.ToBase64String(ReadBytes!, (int)offset, length);
                    return $"{TransferMarker} CHUNK {ReadBytes!.Length} {FixedMtime} {slice}";
                }
            case "WRITE":
                {
                    var offset = long.Parse(Token(markerLine, "off="));
                    var length = long.Parse(Token(markerLine, "len="));
                    return $"{TransferMarker} WROTE {offset + length}";
                }
            case "FINALIZE":
                return WriteFailsIntegrity ? $"{TransferMarker} INTEGRITY" : $"{TransferMarker} FINALIZED";
            case "LIST":
                return ListMissing ? $"{TransferMarker} NOTFOUND" : $"{TransferMarker} OK";
            default:
                // CLEANUP and any other maintenance step.
                return $"{TransferMarker} OK";
        }
    }

    /// <summary>Extracts <c>params.arguments.command</c> from the MCP <c>tools/call</c> request body.</summary>
    private static string ExtractCommandScript(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("params").GetProperty("arguments").GetProperty("command").GetString()
            ?? string.Empty;
    }

    /// <summary>The role token (RUN/PROBE/STAT/…) immediately following a marker prefix.</summary>
    private static string MarkerRole(string markerLine, string prefix)
    {
        var rest = markerLine[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 0 ? string.Empty : rest[0];
    }

    /// <summary>Reads the value of a <c>key=value</c> token from a marker line.</summary>
    private static string Token(string markerLine, string key)
    {
        foreach (var token in markerLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(key, StringComparison.Ordinal))
            {
                return token[key.Length..];
            }
        }

        throw new InvalidOperationException($"Marker line '{markerLine}' is missing token '{key}'.");
    }

    /// <summary>Builds the base64 of a complete inline command manifest whose digest echoes the RUN request.</summary>
    private string BuildManifestBase64(string digest)
    {
        var stdoutBytes = Encoding.UTF8.GetBytes(CommandStdout);
        var stderrBytes = Encoding.UTF8.GetBytes(CommandStderr);

        var manifest = new
        {
            v = 1,
            digest,
            gen = Generation,
            exit = CommandExitCode,
            stdout = StreamManifest(stdoutBytes),
            stderr = StreamManifest(stderrBytes),
            lease = 0,
            created = 0,
        };

        var json = JsonSerializer.Serialize(manifest);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static object StreamManifest(byte[] bytes) =>
        new
        {
            len = bytes.Length,
            sha256 = Sha256Hex(bytes),
            inline = Convert.ToBase64String(bytes),
        };

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpResponseMessage Mcp(string text, bool isError)
    {
        var envelope = new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new
            {
                content = new[] { new { type = "text", text } },
                isError,
            },
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json"),
        };
    }
}
