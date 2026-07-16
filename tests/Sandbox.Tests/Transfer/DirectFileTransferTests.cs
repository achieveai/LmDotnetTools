using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Transfer;

/// <summary>
/// Wire-level tests for the direct files/directories API (ADR 0031 / issue #119) that
/// <see cref="SandboxClient.ReadTextFileAsync"/>, <see cref="SandboxClient.WriteTextFileAsync"/>, and
/// <see cref="SandboxClient.ListDirectoryAsync"/> now speak, driven through the in-memory
/// <see cref="FakeGatewayHandler"/>. Each test proves a genuine wire outcome — exact byte round-tripping,
/// error-code mapping, cursor-paginated listing — rather than how often a collaborator was called.
/// </summary>
public class DirectFileTransferTests
{
    private const string Session = "s1";
    private const long MountId = 7;

    /// <summary>Wires a borrowed client and pre-registers the mount-id resolution route every direct file/directory call depends on (<c>volumes.workspace.id</c> resolves to <see cref="MountId"/>).</summary>
    private static (SandboxClient Client, FakeGatewayHandler Handler) CreateClient()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            $"/api/v1/sandboxes/{Session}",
            """{"session_id":"s1","volumes":{"workspace":{"container_path":"/workspace","read_only":false,"id":7}}}"""
        );
        return (client, handler);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>The <c>operation_id</c> the SDK put in a submit body — the fake must echo it (the SDK generates the mkdir id) so the correlation-id check passes.</summary>
    private static string OperationIdFrom(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("operation_id").GetString()!;
    }

    /// <summary>A terminal <c>succeeded</c> exit-0 operation snapshot echoing the submitted operation id.</summary>
    private static HttpResponseMessage MkdirSucceeded(HttpRequestMessage request) =>
        JsonResponse("{\"operation_id\":\"" + OperationIdFrom(request) + "\",\"status\":\"succeeded\",\"exit_code\":0}");

    [Fact]
    public async Task WriteThenRead_Utf8RoundTrip_ReturnsExactBytes()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        byte[]? stored = null;

        // The PUT captures the exact bytes the SDK sent; the GET echoes back exactly those bytes, so
        // a passing round-trip proves byte-exactness end-to-end rather than just "no exception thrown".
        handler.On(
            req => req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            req =>
            {
                stored = req.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return JsonResponse($$"""{"bytes_written":{{stored.Length}}}""");
            }
        );
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stored!) }
        );

        const string content = "héllo\nwörld\t— 日本語 🌐\nlast";

        await client.WriteTextFileAsync(Session, "notes.txt", content);
        var roundTripped = await client.ReadTextFileAsync(Session, "notes.txt");

        roundTripped.Should().Be(content);
        stored.Should().Equal(Encoding.UTF8.GetBytes(content));
    }

    [Fact]
    public async Task WriteTextFileAsync_PostsExactByteCount_AndAcceptsMatchingBytesWritten()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        byte[]? sentBody = null;

        handler.On(
            req => req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            req =>
            {
                sentBody = req.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return JsonResponse($$"""{"bytes_written":{{sentBody.Length}}}""");
            }
        );

        const string content = "some text — with a dash";

        await client.WriteTextFileAsync(Session, "a.txt", content);

        sentBody.Should().Equal(Encoding.UTF8.GetBytes(content));
        // A top-level (parentless) write is a single PUT — it must never trigger a mkdir operation.
        handler
            .Requests.Should()
            .NotContain(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteTextFileAsync_NestedParentMissing_MkdirsParentThenRetriesPut()
    {
        var (client, handler) = CreateClient();
        using var _ = client;

        var putCount = 0;
        byte[]? stored = null;
        handler.On(
            req => req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            req =>
            {
                putCount++;
                if (putCount == 1)
                {
                    // The parent dir does not exist yet: the direct files PUT streams into a temp sibling
                    // (create_new) without creating the parent, so it 404s path_not_found — no gateway mkdir.
                    return JsonResponse(
                        """{"error":"path not found","code":404,"error_code":"path_not_found","retryable":false}""",
                        HttpStatusCode.NotFound
                    );
                }

                stored = req.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return JsonResponse("{\"bytes_written\":" + stored.Length + "}");
            }
        );
        handler.On(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal),
            MkdirSucceeded
        );

        const string content = "hi\n";
        await client.WriteTextFileAsync(Session, "nested/dir/greeting.txt", content);

        // The 404 self-healed: exactly one `mkdir -p -- nested/dir` operation, then the retried PUT
        // succeeded with the exact bytes.
        stored.Should().Equal(Encoding.UTF8.GetBytes(content));
        putCount.Should().Be(2);

        var mkdirRequest = handler
            .Requests.Should()
            .ContainSingle(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal))
            .Which;
        var op = JsonDocument.Parse(mkdirRequest.Body!).RootElement;
        op.GetProperty("executable").GetString().Should().Be("mkdir");
        op.GetProperty("args").EnumerateArray().Select(e => e.GetString()).Should().Equal("-p", "--", "nested/dir");
        op.GetProperty("cwd").GetProperty("mount_id").GetInt64().Should().Be(MountId);
        op.GetProperty("cwd").GetProperty("path").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task WriteTextFileAsync_MkdirParentBeginningWithDash_TerminatesOptionParsing()
    {
        var (client, handler) = CreateClient();
        using var _ = client;

        var putCount = 0;
        byte[]? stored = null;
        handler.On(
            req => req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            req =>
            {
                putCount++;
                if (putCount == 1)
                {
                    return JsonResponse(
                        """{"error":"path not found","code":404,"error_code":"path_not_found","retryable":false}""",
                        HttpStatusCode.NotFound
                    );
                }

                stored = req.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return JsonResponse("{\"bytes_written\":" + stored.Length + "}");
            }
        );
        handler.On(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal),
            MkdirSucceeded
        );

        // The first path component begins with `-`: without a `--` operand terminator, `mkdir` would parse
        // "-m" as an OPTION, not a directory. The `--` must appear before it.
        const string content = "x";
        await client.WriteTextFileAsync(Session, "-m/greeting.txt", content);

        stored.Should().Equal(Encoding.UTF8.GetBytes(content));
        putCount.Should().Be(2);

        var mkdirRequest = handler
            .Requests.Should()
            .ContainSingle(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal))
            .Which;
        var op = JsonDocument.Parse(mkdirRequest.Body!).RootElement;
        op.GetProperty("args").EnumerateArray().Select(e => e.GetString()).Should().Equal("-p", "--", "-m");
    }

    [Fact]
    public async Task WriteTextFileAsync_BareNotFound_Propagates_WithoutMkdir()
    {
        var (client, handler) = CreateClient();
        using var _ = client;

        var putCount = 0;
        handler.On(
            req => req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ =>
            {
                putCount++;
                // A code-less 404 is AMBIGUOUS (the direct API also 404s an evicted session), so it is NOT
                // a definitive missing path: the write must NOT self-heal it. The original NotFound
                // propagates and no mkdir operation is issued.
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
            }
        );

        Func<Task> act = () => client.WriteTextFileAsync(Session, "nested/dir/greeting.txt", "y");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.NotFound);
        putCount.Should().Be(1); // one PUT, no retry
        handler
            .Requests.Should()
            .NotContain(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteTextFileAsync_MkdirFails_ThrowsOperationFailedWithExitCodeAndStderr()
    {
        var (client, handler) = CreateClient();
        using var _ = client;

        handler.On(
            req => req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => JsonResponse(
                """{"error":"path not found","code":404,"error_code":"path_not_found","retryable":false}""",
                HttpStatusCode.NotFound
            )
        );
        // mkdir -p ran but FAILED (e.g. a read-only parent): a terminal non-zero exit with a stderr
        // artifact. Echo the submitted operation id so the correlation check passes and we reach the
        // artifact download / OperationFailed path.
        handler.On(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal),
            req => JsonResponse(
                "{\"operation_id\":\""
                    + OperationIdFrom(req)
                    + "\",\"status\":\"failed\",\"exit_code\":1,\"artifacts\":{\"mount_id\":7,\"stdout_path\":\"out\",\"stderr_path\":\"err\"}}"
            )
        );
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.Query.Contains("path=out", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) }
        );
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.Query.Contains("path=err", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("mkdir: cannot create directory: Read-only file system") }
        );

        Func<Task> act = () => client.WriteTextFileAsync(Session, "nested/dir/greeting.txt", "z");

        var exception = await act.Should().ThrowAsync<SandboxException>();
        // A ran-fine-but-failed mkdir is an operational failure, NOT a malformed response (Protocol). The
        // exception carries the real exit code, a stderr snippet, and the operation id.
        exception.Which.Kind.Should().Be(SandboxErrorKind.OperationFailed);
        exception.Which.Message.Should().Contain("exited 1");
        exception.Which.Message.Should().Contain("Read-only file system");
        exception.Which.OperationId.Should().NotBeNullOrEmpty(); // the SDK's own generated mkdir op id
    }

    [Fact]
    public async Task WriteTextFileAsync_MkdirSucceededWithNullExit_ThrowsProtocol_AndDoesNotRetry()
    {
        var (client, handler) = CreateClient();
        using var _ = client;

        var putCount = 0;
        handler.On(
            req => req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ =>
            {
                putCount++;
                return JsonResponse(
                    """{"error":"path not found","code":404,"error_code":"path_not_found","retryable":false}""",
                    HttpStatusCode.NotFound
                );
            }
        );
        // mkdir returns a MALFORMED terminal: status succeeded but no exit_code. The self-heal must NOT read
        // that as a false exit 0 and retry the write — it must surface Protocol.
        handler.On(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal),
            req => JsonResponse(
                "{\"operation_id\":\""
                    + OperationIdFrom(req)
                    + "\",\"status\":\"succeeded\",\"artifacts\":{\"mount_id\":7,\"stdout_path\":\"out\",\"stderr_path\":\"err\"}}"
            )
        );

        Func<Task> act = () => client.WriteTextFileAsync(Session, "nested/dir/greeting.txt", "z");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        putCount.Should().Be(1); // the malformed mkdir aborted the write — no retry PUT
    }

    [Fact]
    public async Task ReadTextFileAsync_MissingFile_ThrowsNotFound()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        handler.OnJson(
            HttpMethod.Get,
            $"/files/{MountId}",
            """{"error":"path not found","code":404,"error_code":"path_not_found","retryable":false}""",
            HttpStatusCode.NotFound
        );

        Func<Task> act = () => client.ReadTextFileAsync(Session, "missing.txt");

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.NotFound);
        exception.Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ListDirectoryAsync_Paginated_ReturnsNamesInOrder_IncludingDotfilesAndSpaces_AndThreadsCursor()
    {
        var (client, handler) = CreateClient();
        using var _ = client;

        // First page: no cursor yet. Names deliberately include a dotfile and a space to prove
        // neither is dropped or mis-split.
        handler.On(
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/directories/{MountId}", StringComparison.Ordinal)
                && !req.RequestUri.Query.Contains("cursor=", StringComparison.Ordinal),
            _ =>
                JsonResponse(
                    """{"entries":[{"name":"a b.txt","type":"file","size":1},{"name":".hidden","type":"file","size":1}],"next_cursor":"c1"}"""
                )
        );
        // Second page: only served once the opaque cursor from the first page is threaded back verbatim.
        handler.On(
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/directories/{MountId}", StringComparison.Ordinal)
                && req.RequestUri.Query.Contains("cursor=c1", StringComparison.Ordinal),
            _ => JsonResponse("""{"entries":[{"name":"sub","type":"directory"}]}""")
        );

        var names = await client.ListDirectoryAsync(Session, "");

        names.Should().Equal("a b.txt", ".hidden", "sub");
        handler.Requests.Count(r =>
                r.Method == HttpMethod.Get && r.Uri.AbsolutePath.EndsWith($"/directories/{MountId}", StringComparison.Ordinal)
            )
            .Should()
            .Be(2);
        handler.Requests.Should().Contain(r => r.Uri.Query.Contains("cursor=c1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListDirectoryAsync_DirectoryTooLarge_ThrowsProtocol()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        handler.OnJson(
            HttpMethod.Get,
            $"/directories/{MountId}",
            """{"error":"directory exceeds scan cap","code":400,"error_code":"directory_too_large","retryable":false}""",
            HttpStatusCode.BadRequest
        );

        Func<Task> act = () => client.ListDirectoryAsync(Session, "big-dir");

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        exception.Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ListDirectoryAsync_RepeatedCursor_ThrowsProtocol()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        // Every page hands back the SAME next_cursor — the SDK must reject the repeat rather than loop.
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/directories/{MountId}", StringComparison.Ordinal),
            _ => JsonResponse("""{"entries":[{"name":"a","type":"file"}],"next_cursor":"loop"}""")
        );

        Func<Task> act = () => client.ListDirectoryAsync(Session, "");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ListDirectoryAsync_FreshCursorsPastThePageCap_ThrowsProtocol()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        // Every page hands back a DISTINCT fresh cursor forever — the seen-cursor guard never trips, so the
        // total page cap must, rather than looping/growing unbounded.
        var page = 0;
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/directories/{MountId}", StringComparison.Ordinal),
            _ => JsonResponse("{\"entries\":[],\"next_cursor\":\"c" + Interlocked.Increment(ref page) + "\"}")
        );

        Func<Task> act = () => client.ListDirectoryAsync(Session, "");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ReadTextFileAsync_Redirect_IsRejectedAsProtocol_AndNeverFollowed()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ =>
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("http://malicious.invalid:9999/files/7?path=x");
                return redirect;
            }
        );

        Func<Task> act = () => client.ReadTextFileAsync(Session, "notes.txt");

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        // The SDK never chases the Location — the credentialed request only ever reaches the gateway host.
        handler.Requests.Should().NotContain(r => r.Uri.Host == "malicious.invalid");
    }

    [Fact]
    public async Task ReadTextFileAsync_Unauthorized_IsAuthorization_AndNeverLeaksTheResponseBody()
    {
        // A 401/403 body is the response most likely to echo credential material, so the SDK must
        // classify it WITHOUT reading the body — the sentinel below must never reach the exception.
        const string sentinel = "sk-sandbox-leaked-secret-abc123";
        var (client, handler) = CreateClient();
        using var _ = client;
        handler.OnJson(
            HttpMethod.Get,
            $"/files/{MountId}",
            $$"""{"error":"{{sentinel}}","code":403,"error_code":"forbidden","retryable":false}""",
            HttpStatusCode.Forbidden
        );

        Func<Task> act = () => client.ReadTextFileAsync(Session, "notes.txt");

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.Authorization);
        exception.Which.Message.Should().NotContain(sentinel);
        exception.Which.ToString().Should().NotContain(sentinel);
    }

    [Fact]
    public async Task ReadTextFileAsync_NonUtf8Body_ThrowsIntegrity()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        // 0xFF/0xFE are never valid UTF-8 lead bytes — a strict decode must reject them rather than
        // substituting U+FFFD replacement characters.
        var invalidUtf8 = new byte[] { 0xFF, 0xFE, 0x00, 0x80 };
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(invalidUtf8) }
        );

        Func<Task> act = () => client.ReadTextFileAsync(Session, "binary.bin");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Integrity);
    }

    [Fact]
    public async Task ReadTextFileAsync_CallerCancelsDuringErrorBodyParse_ThrowsOperationCanceled()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        using var callerCts = new CancellationTokenSource();
        // The gateway's error response has arrived, but the CALLER cancels as the SDK starts reading the
        // error body (the content trips the caller's token on first read). Genuine caller cancellation must
        // surface as a plain OperationCanceledException — the documented cancellation contract — NOT be
        // masked as a SandboxException.
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new CancelOnReadContent(
                    """{"error":"locked","code":409,"error_code":"target_locked","retryable":false}""",
                    callerCts
                ),
            }
        );

        Func<Task> act = () => client.ReadTextFileAsync(Session, "notes.txt", callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadTextFileAsync_SdkBodyReadTimeout_CallerNotCancelled_ClassifiesStatusOnly()
    {
        // A short SDK transport deadline; the caller's token is never cancelled.
        var (client, handler) = TestSupport.CreateBorrowedClient(transportTimeout: TimeSpan.FromMilliseconds(150));
        using var _ = client;
        handler.OnJson(
            HttpMethod.Get,
            $"/api/v1/sandboxes/{Session}",
            """{"session_id":"s1","volumes":{"workspace":{"container_path":"/workspace","read_only":false,"id":7}}}"""
        );
        // The error response has arrived (409), but its body never finishes streaming. The SDK's OWN
        // body-read deadline must fire (caller NOT cancelled) and fall back to status-only classification —
        // an already-received gateway error is never lost to an SDK-internal timeout, and this must NOT
        // surface as cancellation.
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new NeverEndingContent() }
        );

        Func<Task> act = () => client.ReadTextFileAsync(Session, "notes.txt");

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.Conflict);
    }

    [Fact]
    public async Task ReadTextFileAsync_SlowHeadersThenHangingErrorBody_BoundedByOneTransportTimeout()
    {
        // Wide absolute margins so CI/timer jitter never flips the assertion: the fixed one-timeout call
        // is ~1000 ms, the old double-timeout bug would be ~1600 ms (headerDelay + a second full timeout),
        // and the ceiling sits at 1500 ms — clearly under the ~2000 ms double budget yet above ~1000 ms.
        var transportTimeout = TimeSpan.FromMilliseconds(1000);
        var headerDelay = TimeSpan.FromMilliseconds(600);
        var serverAddress = TestSupport.NewLoopbackAddress();
        using var httpClient = new HttpClient(new SlowHeaderHangingBodyHandler(headerDelay)) { BaseAddress = serverAddress };
        var options = new SandboxClientOptions(serverAddress, "app-1", TestSupport.ValidSecret, TimeSpan.FromMinutes(5), transportTimeout);
        using var client = new SandboxClient(options, httpClient);

        var stopwatch = Stopwatch.StartNew();
        Func<Task> act = () => client.ReadTextFileAsync(Session, "notes.txt");
        var exception = await act.Should().ThrowAsync<SandboxException>();
        stopwatch.Stop();

        // The slow headers consumed part of the ONE transport budget, and the error-body read shares the
        // SAME budget, so the whole call is bounded by ~1× TransportTimeout. The old double-timeout bug
        // (a fresh TransportTimeout for the error body) would have taken ~headerDelay + TransportTimeout
        // (≈ 1600 ms here) — comfortably above this ceiling.
        exception.Which.Kind.Should().Be(SandboxErrorKind.Conflict);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task ReadTextFileAsync_ContentLengthOverCap_ThrowsProtocol_BeforeBuffering()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        // The gateway declares a body far larger than the SDK's in-memory read cap. The SDK must refuse
        // it by its declared Content-Length BEFORE buffering a single byte (the content below would
        // otherwise never actually produce those bytes).
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new OversizedContent(SandboxClient.MaxDirectReadBytes + 1) }
        );

        Func<Task> act = () => client.ReadTextFileAsync(Session, "huge.bin");

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ReadTextFileAsync_ChunkedBodyOverCap_ThrowsProtocol_WhileStreaming()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        // No Content-Length at all (chunked): the header pre-check cannot catch this, so only the
        // streaming byte counter can — it must reject the body the instant it streams past the cap rather
        // than buffering the whole thing. The stream produces zero bytes lazily, so nothing is allocated
        // up front.
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedStreamContent(SandboxClient.MaxDirectReadBytes + 1) }
        );

        Func<Task> act = () => client.ReadTextFileAsync(Session, "huge-chunked.bin");

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ReadTextFileAsync_SessionNotFound_EvictsMountCache_SoNextReadReresolves()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        // Every file GET reports the session gone. The first read caches the mount (via the pre-registered
        // GET /sandboxes/s1 route) then fails session_not_found, which must evict that cache entry; a
        // second read therefore re-resolves the mount with a FRESH GET rather than replaying a dead mapping.
        handler.OnJson(
            HttpMethod.Get,
            $"/files/{MountId}",
            """{"error":"session gone","code":404,"error_code":"session_not_found","retryable":false}""",
            HttpStatusCode.NotFound
        );

        await Assert.ThrowsAsync<SandboxException>(() => client.ReadTextFileAsync(Session, "a.txt"));
        await Assert.ThrowsAsync<SandboxException>(() => client.ReadTextFileAsync(Session, "b.txt"));

        handler
            .Requests.Count(r =>
                r.Method == HttpMethod.Get
                && r.Uri.AbsolutePath.EndsWith($"/sandboxes/{Session}", StringComparison.Ordinal)
            )
            .Should()
            .Be(2);
    }

    /// <summary>
    /// An <see cref="HttpContent"/> that DECLARES a large <c>Content-Length</c> (via
    /// <see cref="TryComputeLength"/>) without allocating any bytes, so the SDK's pre-read size guard can
    /// be exercised without materializing a real oversize body.
    /// </summary>
    private sealed class OversizedContent : HttpContent
    {
        private readonly long _length;

        public OversizedContent(long length) => _length = length;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }

    /// <summary>
    /// A JSON <see cref="HttpContent"/> that cancels a supplied token the instant its body starts being
    /// read — simulating a caller that cancels AFTER the (already-received) error response, right as the
    /// SDK reads the error body. The bytes are still delivered, so a body read that does NOT observe the
    /// cancelled token succeeds.
    /// </summary>
    private sealed class CancelOnReadContent : HttpContent
    {
        private readonly byte[] _json;
        private readonly CancellationTokenSource _cancelOnRead;

        public CancelOnReadContent(string json, CancellationTokenSource cancelOnRead)
        {
            _json = Encoding.UTF8.GetBytes(json);
            _cancelOnRead = cancelOnRead;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            _cancelOnRead.Cancel();
            return Task.FromResult<Stream>(new MemoryStream(_json, writable: false));
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            _cancelOnRead.Cancel();
            return new MemoryStream(_json, writable: false).CopyToAsync(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _json.Length;
            return true;
        }
    }

    /// <summary>
    /// A JSON <see cref="HttpContent"/> whose body stream never finishes (each read blocks until its token
    /// is cancelled) — simulating an error response whose body hangs, so the SDK's OWN body-read deadline
    /// must be what ends the read.
    /// </summary>
    private sealed class NeverEndingContent : HttpContent
    {
        public NeverEndingContent() => Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new NeverEndingStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.Delay(Timeout.Infinite);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A read stream whose every read blocks until the supplied token cancels (then throws), so nothing is ever produced.</summary>
    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Answers mount resolution immediately, but DELAYS the (error) files-response headers by
    /// <paramref name="headerDelay"/> — consuming part of the transport budget — then returns a non-2xx
    /// whose error body never finishes streaming (<see cref="NeverEndingContent"/>). Used to prove the
    /// whole download is bounded by ONE TransportTimeout across headers + error body.
    /// </summary>
    private sealed class SlowHeaderHangingBodyHandler(TimeSpan headerDelay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/files/", StringComparison.Ordinal))
            {
                await Task.Delay(headerDelay, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new NeverEndingContent() };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"session_id":"s1","volumes":{"workspace":{"container_path":"/workspace","read_only":false,"id":7}}}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        }
    }

    /// <summary>
    /// An <see cref="HttpContent"/> that reports NO <c>Content-Length</c> (<see cref="TryComputeLength"/>
    /// returns <c>false</c>, so the response looks chunked) and whose read stream lazily yields a fixed
    /// number of zero bytes without allocating them — exercising the SDK's STREAMING byte cap (not the
    /// header pre-check) with negligible up-front memory.
    /// </summary>
    private sealed class UnsizedStreamContent : HttpContent
    {
        private readonly long _length;

        public UnsizedStreamContent(long length) => _length = length;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            new ZeroStream(_length).CopyToAsync(stream);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ZeroStream(_length));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A read-only forward stream that yields <paramref name="length"/> zero bytes then EOF, without allocating them.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var produced = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, produced);
            _remaining -= produced;
            return produced;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
