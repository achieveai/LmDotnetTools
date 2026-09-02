using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// Tests for the operation-record RELEASE that <see cref="SandboxClient.ExecuteAsync"/> performs once a
/// command's terminal result is fully in hand (issue #725): the gateway keeps at most
/// <c>OPERATION_MAX_RECORDS_PER_SESSION</c> (default 256) tracked records per session and its reaper
/// prunes terminal ones only after <c>OPERATION_TERMINAL_TTL_SECS</c> (default 3600), so a session that
/// never deletes its records is refused with <c>503 operation_capacity_exhausted</c> long before the TTL
/// frees them. ADR 0031 §5 makes <c>DELETE .../operations/{operation_id}</c> the explicit lifecycle exit.
/// <para>
/// The release is scoped to the ids the SDK MINTS. A caller-supplied
/// <see cref="SandboxCommand.OperationId"/> is the caller's statement that it owns the record's
/// lifecycle — replaying it, reading its artifacts, and deleting it — so the SDK must leave that record
/// alone. Both branches are pinned below; they are the same line of code read two ways.
/// </para>
/// </summary>
public sealed class OperationReleaseTests
{
    /// <summary>The gateway's own <c>OPERATION_MAX_RECORDS_PER_SESSION</c> default, mirrored by the fake below.</summary>
    private const int MaxRecordsPerSession = 256;

    private static void RegisterWorkspaceMount(FakeGatewayHandler handler, string sessionId, long mountId) =>
        handler.On(
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/sandboxes/{sessionId}", StringComparison.Ordinal),
            _ =>
                Json(
                    "{\"session_id\":\""
                        + sessionId
                        + "\",\"container_id\":null,\"volumes\":{\"workspace\":{\"container_path\":\"/workspace\",\"read_only\":false,\"id\":"
                        + mountId
                        + "}}}"
                )
        );

    /// <summary>
    /// Answers every submit with a terminal snapshot ECHOING the submitted operation id. Most tests here
    /// exercise the SDK-minted branch, where the id is a fresh GUID the test cannot know up front, so the
    /// fake reads it back off the request exactly as the real gateway does.
    /// </summary>
    private static void RegisterTerminalSubmit(FakeGatewayHandler handler, long mountId) =>
        handler.On(
            req =>
                req.Method == HttpMethod.Post
                && req.RequestUri!.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal),
            _ => Json(TerminalSnapshot(OperationIdFromBody(handler.Requests[^1].Body!), mountId), HttpStatusCode.OK)
        );

    private static void RegisterDownloads(FakeGatewayHandler handler, string stdout = "", string stderr = "")
    {
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.Query.Contains("path=out", StringComparison.Ordinal),
            _ => Text(stdout)
        );
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.Query.Contains("path=err", StringComparison.Ordinal),
            _ => Text(stderr)
        );
    }

    private static string TerminalSnapshot(string operationId, long mountId) =>
        "{\"operation_id\":\""
        + operationId
        + "\",\"status\":\"succeeded\",\"exit_code\":0,\"artifacts\":{\"mount_id\":"
        + mountId
        + ",\"stdout_path\":\"out\",\"stderr_path\":\"err\"}}";

    private static bool IsDelete(HttpRequestMessage req) => req.Method == HttpMethod.Delete;

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    [Fact]
    public async Task ExecuteAsync_WithAnSdkMintedOperationId_DeletesTheRecordAfterTheResultIsRead()
    {
        const string sessionId = "sess-rel";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        RegisterWorkspaceMount(handler, sessionId, mountId: 4);
        RegisterTerminalSubmit(handler, mountId: 4);
        RegisterDownloads(handler, stdout: "done");
        handler.On(IsDelete, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        // No operationId: the SDK mints one, so nothing outside this call can ever name that record and
        // the SDK is the only party left that could reclaim it.
        var result = await client.ExecuteAsync(sessionId, new SandboxCommand(["echo", "hi"]));

        result.StandardOutput.Should().Be("done");
        result.OperationRecordReleased.Should().BeTrue();
        var delete = handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete).Subject;
        delete.Uri.AbsolutePath.Should().Be($"/api/v1/sandboxes/{sessionId}/operations/{result.OperationId}");
        // Ordering is load-bearing, not incidental: the gateway's DELETE also removes the operation's
        // generation-scoped stdout/stderr artifact directory, so releasing before both downloads have
        // completed would destroy the very output this call returns.
        var deleteIndex = handler.Requests.FindIndex(r => r.Method == HttpMethod.Delete);
        var lastDownloadIndex = handler.Requests.FindLastIndex(r =>
            r.Method == HttpMethod.Get && r.Uri.Query.Contains("path=", StringComparison.Ordinal)
        );
        deleteIndex.Should().BeGreaterThan(lastDownloadIndex);
    }

    [Fact]
    public async Task ExecuteAsync_WithACallerSuppliedOperationId_LeavesTheRecordAloneForItsOwner()
    {
        const string sessionId = "sess-owned";
        const string operationId = "op-owned";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        RegisterWorkspaceMount(handler, sessionId, mountId: 5);
        RegisterTerminalSubmit(handler, mountId: 5);
        RegisterDownloads(handler, stdout: "replayable");
        // A DELETE route that WOULD succeed. The assertion below is therefore about the SDK declining to
        // send one, not about a route that could not have answered.
        handler.On(IsDelete, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await client.ExecuteAsync(sessionId, new SandboxCommand(["echo", "hi"], operationId: operationId));

        // Supplying the id is the caller's claim on the record's lifecycle: re-submitting the id must
        // replay this operation instead of re-running a side-effecting command, its artifacts must stay
        // readable under .mcp-gateway/operations/<id>/, and the caller decides when the delete happens.
        // Reclaiming it here would silently break all three.
        result.StandardOutput.Should().Be("replayable");
        result.OperationId.Should().Be(operationId);
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
        result.OperationRecordReleased.Should().BeFalse("the caller owns this record, so the SDK released nothing");
    }

    [Fact]
    public async Task ExecuteAsync_ReleaseRejectedByGateway_StillReturnsTheResult_AndReportsTheRetainedRecord()
    {
        const string sessionId = "sess-relfail";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        RegisterWorkspaceMount(handler, sessionId, mountId: 2);
        RegisterTerminalSubmit(handler, mountId: 2);
        RegisterDownloads(handler, stdout: "kept");
        handler.On(
            IsDelete,
            _ =>
                Json(
                    """{"error":"gateway busy","code":503,"error_code":"gateway_busy","retryable":true}""",
                    HttpStatusCode.ServiceUnavailable
                )
        );

        var result = await client.ExecuteAsync(sessionId, new SandboxCommand(["echo", "hi"]));

        // The command SUCCEEDED and its output is already downloaded; a failed cleanup must never be
        // promoted into the caller's failure. The caller learns about it from the result instead.
        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Be("kept");
        result.OperationRecordReleased.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ReleaseAnswered404_CountsAsReleased_BecauseThereIsNothingLeftToReclaim()
    {
        const string sessionId = "sess-rel404";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        RegisterWorkspaceMount(handler, sessionId, mountId: 6);
        RegisterTerminalSubmit(handler, mountId: 6);
        RegisterDownloads(handler);
        // ADR 0031 §6's no-existence-oracle boundary: one uniform 404 covers an already-deleted record, a
        // TTL-pruned one, and one dropped by a gateway restart alike. None of those is a retained record.
        handler.On(
            IsDelete,
            _ =>
                Json(
                    """{"error":"not found","code":404,"error_code":"operation_not_found","retryable":false}""",
                    HttpStatusCode.NotFound
                )
        );

        var result = await client.ExecuteAsync(sessionId, new SandboxCommand(["echo", "hi"]));

        // Both halves matter: the delete really was attempted (otherwise "released" would be a claim about
        // a request nobody sent), and the 404 answer counts as released rather than as a retained record.
        handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete);
        result.OperationRecordReleased.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_CancelledWhileTheReleaseIsInFlight_ReturnsTheResultInsteadOfThrowing()
    {
        const string sessionId = "sess-relcancel";
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        using var cts = new CancellationTokenSource();
        RegisterWorkspaceMount(handler, sessionId, mountId: 8);
        RegisterTerminalSubmit(handler, mountId: 8);
        RegisterDownloads(handler, stdout: "survived");
        // Deliberately side-effecting predicate: it fires while the release DELETE is in flight and
        // nothing earlier, so the cancellation lands squarely inside the release rather than before it
        // starts (which would cancel the submit/downloads instead and prove nothing). The matched route
        // then hangs on the now-cancelled linked token.
        handler.OnHang(req =>
        {
            if (req.Method != HttpMethod.Delete)
            {
                return false;
            }

            cts.Cancel();
            return true;
        });

        var result = await client.ExecuteAsync(sessionId, new SandboxCommand(["echo", "hi"]), cts.Token);

        result.StandardOutput.Should().Be("survived");
        result.OperationRecordReleased.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_MoreCommandsThanThePerSessionRecordCap_NeverHitsCapacityExhausted()
    {
        const string sessionId = "sess-cap";
        const int commandCount = MaxRecordsPerSession + 44;
        var (client, handler) = TestSupport.CreateBorrowedClient();
        using var _ = client;
        RegisterWorkspaceMount(handler, sessionId, mountId: 1);

        // A stateful stand-in for the gateway's operation manager: a NEW key is refused with the same
        // retryable 503 the real gateway returns once the session already holds MaxRecordsPerSession
        // records (crates/mcp-gateway/src/operation_manager.rs::reserve_detailed), and DELETE is the only
        // thing that gives a slot back inside the terminal TTL.
        var liveRecords = new HashSet<string>(StringComparer.Ordinal);
        handler.On(
            req =>
                req.Method == HttpMethod.Post
                && req.RequestUri!.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal),
            _ =>
            {
                var submitted = OperationIdFromBody(handler.Requests[^1].Body!);
                if (!liveRecords.Contains(submitted) && liveRecords.Count >= MaxRecordsPerSession)
                {
                    return Json(
                        """{"error":"too many operation records","code":503,"error_code":"operation_capacity_exhausted","retryable":true}""",
                        HttpStatusCode.ServiceUnavailable
                    );
                }

                liveRecords.Add(submitted);
                return Json(TerminalSnapshot(submitted, mountId: 1), HttpStatusCode.OK);
            }
        );
        RegisterDownloads(handler);
        handler.On(
            IsDelete,
            _ => new HttpResponseMessage(
                liveRecords.Remove(handler.Requests[^1].Uri.Segments[^1])
                    ? HttpStatusCode.NoContent
                    : HttpStatusCode.NotFound
            )
        );

        // SDK-minted ids throughout — this is the shape ConversationTranscriptWriter and every other
        // fire-and-forget caller runs, and the only shape the automatic release applies to.
        for (var i = 0; i < commandCount; i++)
        {
            var result = await client.ExecuteAsync(sessionId, new SandboxCommand(["sh", "-c", "true"]));
            result.OperationRecordReleased.Should().BeTrue($"command {i} must release its record");
        }

        // The session ran well past the cap without a single 503: every completed command handed its slot
        // back immediately instead of waiting out the hour-long terminal TTL.
        liveRecords.Should().BeEmpty();
        handler.Requests.Count(r => r.Method == HttpMethod.Delete).Should().Be(commandCount);
    }

    private static string OperationIdFromBody(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("operation_id").GetString()!;
    }
}
