using System.Net;
using System.Text;
using System.Text.Json;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// A deterministic <see cref="HttpMessageHandler"/> that speaks JUST enough of the Sandbox SDK's
/// direct gateway REST protocol (ADR 0031 / issue #119) to drive a real <c>SandboxClient</c>
/// end-to-end from the daemon test assembly. Every flow the daemon adapter exercises is modelled:
/// workspace mount-id resolution, a command via the operations API (submit → terminal snapshot →
/// stdout/stderr artifact download), a file read/write via the files API, and a directory listing.
/// </summary>
/// <remarks>
/// <para>
/// The handler is single-purpose per test — a command scenario only drives the operations + artifact
/// routes, a file scenario only the files/directories routes — so the command and transfer
/// configuration coexist without clashing. It returns a terminal operation snapshot directly from the
/// <c>POST .../operations</c> submit (an idempotent-replay-shaped 200), so no poll round-trip is
/// needed.
/// </para>
/// </remarks>
internal sealed class ScriptedSandboxGateway : HttpMessageHandler
{
    /// <summary>The workspace mount id every direct route is keyed by; surfaced by the mount-resolution route.</summary>
    private const long WorkspaceMountId = 7;

    private const string StdoutArtifactPath = ".mcp-gateway/operations/op/stdout";
    private const string StderrArtifactPath = ".mcp-gateway/operations/op/stderr";

    // ── Command flow configuration ──────────────────────────────────────────────────────────────
    public int CommandExitCode { get; init; }
    public string CommandStdout { get; init; } = string.Empty;
    public string CommandStderr { get; init; } = string.Empty;

    /// <summary>When true, the operation terminalizes as <c>timed_out</c> (the SDK surfaces <c>ExecutionTimeout</c>).</summary>
    public bool SimulateExecutionTimeout { get; init; }

    // ── Transfer flow configuration ─────────────────────────────────────────────────────────────
    /// <summary>Bytes a file read serves, or (for a listing) the NUL-delimited entry names.</summary>
    public byte[]? ReadBytes { get; init; }

    /// <summary>When true, a file read reports the target missing (the SDK surfaces <c>NotFound</c>).</summary>
    public bool ReadMissing { get; init; }

    /// <summary>When true, a directory listing reports the directory missing (the SDK surfaces <c>NotFound</c>).</summary>
    public bool ListMissing { get; init; }

    /// <summary>
    /// When true, a file read / directory listing reports the SESSION gone (<c>session_not_found</c>) rather
    /// than a missing path — the SDK surfaces <c>NotFound</c> with <c>ErrorCode == "session_not_found"</c>,
    /// which the adapter must NOT swallow as an empty read/listing.
    /// </summary>
    public bool SessionEvicted { get; init; }

    /// <summary>
    /// When true, a file read / directory listing reports a BARE 404 with NO machine-readable
    /// <c>error_code</c> (a legacy/older gateway) — the SDK surfaces <c>NotFound</c> with
    /// <c>ErrorCode == null</c>, which the adapter must still degrade to null/empty.
    /// </summary>
    public bool BareNotFound { get; init; }

    /// <summary>When true, a write fails at the gateway (the SDK surfaces a <see cref="AchieveAi.LmDotnetTools.Sandbox.SandboxException"/>).</summary>
    public bool WriteFailsIntegrity { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;

        if (path.Contains("/operations", StringComparison.Ordinal))
        {
            return await RespondToOperationAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (path.Contains("/files/", StringComparison.Ordinal))
        {
            return await RespondToFileAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (path.Contains("/directories/", StringComparison.Ordinal))
        {
            return RespondToDirectory();
        }

        if (request.Method == HttpMethod.Get && path.Contains("/sandboxes/", StringComparison.Ordinal))
        {
            return ResolveWorkspaceMount();
        }

        return new HttpResponseMessage(HttpStatusCode.NotImplemented)
        {
            Content = new StringContent($"No scripted route for {request.Method} {request.RequestUri}"),
        };
    }

    /// <summary>Answers <c>GET /api/v1/sandboxes/{id}</c> with a workspace volume carrying the mount id.</summary>
    private static HttpResponseMessage ResolveWorkspaceMount() =>
        Json(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(
                new
                {
                    session_id = "sess-1",
                    container_id = (string?)null,
                    volumes = new { workspace = new { container_path = "/workspace", read_only = false, id = WorkspaceMountId } },
                }
            )
        );

    /// <summary>
    /// Answers <c>POST .../operations</c> with a terminal snapshot (no poll needed), ECHOING the
    /// submitted <c>operation_id</c> so the SDK's correlation-id check passes (the SDK generates the id).
    /// </summary>
    private async Task<HttpResponseMessage> RespondToOperationAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var operationId = await ResolveOperationIdAsync(request, ct).ConfigureAwait(false);

        if (SimulateExecutionTimeout)
        {
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { operation_id = operationId, status = "timed_out" }));
        }

        var snapshot = new
        {
            operation_id = operationId,
            status = CommandExitCode == 0 ? "succeeded" : "failed",
            exit_code = CommandExitCode,
            artifacts = new
            {
                mount_id = WorkspaceMountId,
                stdout_path = StdoutArtifactPath,
                stderr_path = StderrArtifactPath,
            },
        };
        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(snapshot));
    }

    /// <summary>Extracts the operation id the SDK sent — from the submit body (POST) or the poll path's last segment (GET).</summary>
    private static async Task<string> ResolveOperationIdAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method == HttpMethod.Post && request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("operation_id", out var idProp) && idProp.GetString() is { } id)
            {
                return id;
            }
        }

        var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? Uri.UnescapeDataString(segments[^1]) : "op";
    }

    /// <summary>Answers the files API: a <c>PUT</c> write, a stdout/stderr artifact download, or a user file read.</summary>
    private async Task<HttpResponseMessage> RespondToFileAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = PathParam(request.RequestUri!);

        if (request.Method == HttpMethod.Put)
        {
            if (WriteFailsIntegrity)
            {
                return Error(HttpStatusCode.Conflict, "target_locked");
            }

            var sent = request.Content is null ? 0 : (await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false)).Length;
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { bytes_written = sent }));
        }

        // GET: an operation artifact download, or a user file read.
        if (path.EndsWith("/stdout", StringComparison.Ordinal))
        {
            return Octet(Encoding.UTF8.GetBytes(CommandStdout));
        }

        if (path.EndsWith("/stderr", StringComparison.Ordinal))
        {
            return Octet(Encoding.UTF8.GetBytes(CommandStderr));
        }

        if (SessionEvicted)
        {
            return Error(HttpStatusCode.NotFound, "session_not_found");
        }

        if (BareNotFound)
        {
            return BareNotFoundResponse();
        }

        if (ReadMissing || ReadBytes is null)
        {
            return Error(HttpStatusCode.NotFound, "path_not_found");
        }

        return Octet(ReadBytes);
    }

    /// <summary>Answers <c>GET .../directories/{id}</c> with a single page of entries (NUL-split from <see cref="ReadBytes"/>).</summary>
    private HttpResponseMessage RespondToDirectory()
    {
        if (SessionEvicted)
        {
            return Error(HttpStatusCode.NotFound, "session_not_found");
        }

        if (BareNotFound)
        {
            return BareNotFoundResponse();
        }

        if (ListMissing)
        {
            return Error(HttpStatusCode.NotFound, "path_not_found");
        }

        var names = ReadBytes is null || ReadBytes.Length == 0
            ? []
            : Encoding.UTF8.GetString(ReadBytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);

        var entries = new object[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            entries[i] = new { name = names[i], type = "file" };
        }

        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { entries }));
    }

    /// <summary>Extracts and URL-decodes the <c>path</c> query value of a files/directories request.</summary>
    private static string PathParam(Uri uri)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("path=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair["path=".Length..]);
            }
        }

        return string.Empty;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Octet(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static HttpResponseMessage Error(HttpStatusCode status, string errorCode) =>
        Json(
            status,
            JsonSerializer.Serialize(new { error = errorCode, code = (int)status, error_code = errorCode, retryable = false })
        );

    /// <summary>A bare 404 with a NON-JSON body — no machine-readable <c>error_code</c>, mimicking a legacy/older gateway.</summary>
    private static HttpResponseMessage BareNotFoundResponse() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent("not found", Encoding.UTF8, "text/plain") };
}
