using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Transfer;

/// <summary>
/// Wire-level tests for the direct files/directories API (ADR 0031 / issue #119) that
/// <see cref="SandboxClient.ReadTextFileAsync(string, string, CancellationToken)"/>,
/// <see cref="SandboxClient.WriteTextFileAsync"/>, and
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
        JsonResponse(
            "{\"operation_id\":\"" + OperationIdFrom(request) + "\",\"status\":\"succeeded\",\"exit_code\":0}"
        );

    [Fact]
    public async Task WriteThenRead_Utf8RoundTrip_ReturnsExactBytes()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        byte[]? stored = null;

        // The PUT captures the exact bytes the SDK sent; the GET echoes back exactly those bytes, so
        // a passing round-trip proves byte-exactness end-to-end rather than just "no exception thrown".
        handler.On(
            req =>
                req.Method == HttpMethod.Put
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            req =>
            {
                stored = req.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return JsonResponse($$"""{"bytes_written":{{stored.Length}}}""");
            }
        );
        handler.On(
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stored!) }
        );

        const string content = "héllo\nwörld\t— 日本語 🌐\nlast";

        await client.WriteTextFileAsync(Session, "notes.txt", content);
        var roundTripped = await client.ReadTextFileAsync(Session, "notes.txt");

        roundTripped.Should().Be(content);
        stored.Should().Equal(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// The line-ending and boundary shapes #190 calls out by name. Under the shipped direct-files design
    /// the SDK sends the file's exact bytes in one PUT and decodes exactly what the GET returns, so the
    /// only way these could be corrupted is a normalizing encode/decode — which is precisely what a
    /// strict, no-BOM UTF-8 codec must never do. Each case round-trips through the captured PUT body, so
    /// a passing assertion proves byte-exactness on the wire, not just string equality after the fact.
    /// </summary>
    [Theory]
    // CRLF must survive verbatim — never normalized to LF.
    [InlineData("line one\r\nline two\r\n")]
    // A lone CR (classic-Mac ending) is neither expanded nor swallowed.
    [InlineData("alpha\rbeta\rgamma")]
    // No final newline: the last line must NOT gain one.
    [InlineData("no trailing newline")]
    // A trailing newline must NOT be stripped.
    [InlineData("has trailing newline\n")]
    // A file that is nothing but line endings.
    [InlineData("\r\n\n\r")]
    // The empty file: zero bytes out, empty string back — not a null, not a failure.
    [InlineData("")]
    // Mixed endings in one document stay exactly as authored.
    [InlineData("crlf\r\nlf\ncr\rend")]
    public async Task WriteThenRead_PreservesExactLineEndingsAndBoundaries(string content)
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        byte[]? stored = null;

        handler.On(
            req =>
                req.Method == HttpMethod.Put
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            req =>
            {
                stored = req.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return JsonResponse($$"""{"bytes_written":{{stored.Length}}}""");
            }
        );
        handler.On(
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stored!) }
        );

        await client.WriteTextFileAsync(Session, "endings.txt", content);
        var roundTripped = await client.ReadTextFileAsync(Session, "endings.txt");

        // The bytes on the wire are the caller's exact UTF-8 — no BOM prefixed, no ending rewritten.
        stored.Should().Equal(Encoding.UTF8.GetBytes(content));
        roundTripped.Should().Be(content);
    }

    [Fact]
    public async Task WriteThenRead_LargeDocument_RoundTripsExactly_InOneRequestEach()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        byte[]? stored = null;

        handler.On(
            req =>
                req.Method == HttpMethod.Put
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            req =>
            {
                stored = req.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return JsonResponse($$"""{"bytes_written":{{stored.Length}}}""");
            }
        );
        handler.On(
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stored!) }
        );

        // ~1.6 MiB of mixed-width UTF-8: every multibyte sequence lands at a different byte offset in each
        // line, so any framing that split the payload on a fixed boundary without re-joining the code
        // points would corrupt it. The predecessor design chunked base64 at ~12 KiB; this proves the
        // shipped single-request design carries the same content intact.
        var builder = new StringBuilder();
        for (var i = 0; i < 20_000; i++)
        {
            builder.Append("行 ").Append(i).Append(" — naïve café 🌐 payload\r\n");
        }

        var content = builder.ToString();

        await client.WriteTextFileAsync(Session, "big.txt", content);
        var roundTripped = await client.ReadTextFileAsync(Session, "big.txt");

        roundTripped.Should().Be(content);
        stored.Should().Equal(Encoding.UTF8.GetBytes(content));
        // Exactly one PUT and one GET: the direct files API transfers a whole file per request, so a
        // repeated write would mean the SDK re-sent a side-effecting request it must only send once.
        handler.Requests.Count(r => r.Method == HttpMethod.Put).Should().Be(1);
        handler
            .Requests.Count(r =>
                r.Method == HttpMethod.Get && r.Uri.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal)
            )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task WriteTextFileAsync_UnpairedSurrogate_ThrowsArgumentExceptionNamingContent_AndSendsNothing()
    {
        var (client, handler) = CreateClient();
        using var _ = client;

        // A lone high surrogate has no UTF-8 encoding. The strict (throwing) encoder refuses it — and that
        // refusal must reach the caller as a NAMED argument failure about `content`, not as a raw
        // EncoderFallbackException whose ParamName is null and whose message names only a character index.
        var content = "before " + (char)0xD83C + " after";

        Func<Task> act = () => client.WriteTextFileAsync(Session, "bad.txt", content);

        var thrown = await act.Should().ThrowAsync<ArgumentException>();
        thrown.And.ParamName.Should().Be("content");
        // The original encoder failure is preserved, not swallowed: the CHANGELOG promises callers can still
        // reach the character index the strict encoder objected to, which lives only on the inner exception.
        thrown.And.InnerException.Should().BeOfType<EncoderFallbackException>();
        // Refused before anything left the process: the target file is untouched and no PUT was issued.
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ListDirectoryAsync_NamesBearingCarriageReturnLineFeedAndTab_SurviveVerbatim()
    {
        var (client, handler) = CreateClient();
        using var _ = client;

        // POSIX permits every byte except '/' and NUL in a filename, so CR, LF and TAB are all legal.
        // The predecessor design framed listings as newline-delimited text, where such a name would split
        // into two bogus entries; the shipped JSON listing must return each name as one exact string.
        handler.On(
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/directories/{MountId}", StringComparison.Ordinal),
            _ =>
                JsonResponse(
                    """
                    {"entries":[
                      {"name":"two\nlines.txt","type":"file","size":1},
                      {"name":"carriage\rreturn.txt","type":"file","size":1},
                      {"name":"tab\there.txt","type":"file","size":1},
                      {"name":"crlf\r\nboth.txt","type":"file","size":1}
                    ]}
                    """
                )
        );

        var names = await client.ListDirectoryAsync(Session, "");

        names.Should().Equal("two\nlines.txt", "carriage\rreturn.txt", "tab\there.txt", "crlf\r\nboth.txt");
    }

    [Fact]
    public async Task WriteTextFileAsync_PostsExactByteCount_AndAcceptsMatchingBytesWritten()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        byte[]? sentBody = null;

        handler.On(
            req =>
                req.Method == HttpMethod.Put
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
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
            .NotContain(r =>
                r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal)
            );
    }

    [Fact]
    public async Task WriteTextFileAsync_NestedParentMissing_MkdirsParentThenRetriesPut()
    {
        var (client, handler) = CreateClient();
        using var _ = client;

        var putCount = 0;
        byte[]? stored = null;
        handler.On(
            req =>
                req.Method == HttpMethod.Put
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
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
            req =>
                req.Method == HttpMethod.Post
                && req.RequestUri!.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal),
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
            .ContainSingle(r =>
                r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal)
            )
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
            req =>
                req.Method == HttpMethod.Put
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
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
            req =>
                req.Method == HttpMethod.Post
                && req.RequestUri!.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal),
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
            .ContainSingle(r =>
                r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal)
            )
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
            req =>
                req.Method == HttpMethod.Put
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
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
            .NotContain(r =>
                r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal)
            );
    }

    [Fact]
    public async Task WriteTextFileAsync_MkdirFails_ThrowsOperationFailedWithExitCodeAndStderr()
    {
        var (client, handler) = CreateClient();
        using var _ = client;

        handler.On(
            req =>
                req.Method == HttpMethod.Put
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ =>
                JsonResponse(
                    """{"error":"path not found","code":404,"error_code":"path_not_found","retryable":false}""",
                    HttpStatusCode.NotFound
                )
        );
        // mkdir -p ran but FAILED (e.g. a read-only parent): a terminal non-zero exit with a stderr
        // artifact. Echo the submitted operation id so the correlation check passes and we reach the
        // artifact download / OperationFailed path.
        handler.On(
            req =>
                req.Method == HttpMethod.Post
                && req.RequestUri!.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal),
            req =>
                JsonResponse(
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
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("mkdir: cannot create directory: Read-only file system"),
            }
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
            req =>
                req.Method == HttpMethod.Put
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
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
            req =>
                req.Method == HttpMethod.Post
                && req.RequestUri!.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal),
            req =>
                JsonResponse(
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
        handler
            .Requests.Count(r =>
                r.Method == HttpMethod.Get
                && r.Uri.AbsolutePath.EndsWith($"/directories/{MountId}", StringComparison.Ordinal)
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
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/directories/{MountId}", StringComparison.Ordinal),
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
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/directories/{MountId}", StringComparison.Ordinal),
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
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
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
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
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
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
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
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new NeverEndingContent() }
        );

        Func<Task> act = () => client.ReadTextFileAsync(Session, "notes.txt");

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.Conflict);
    }

    [Fact]
    public async Task ReadTextFileAsync_SlowHeadersThenHangingErrorBody_BoundedByOneTransportTimeout()
    {
        // The invariant under test: the error-body read must SHARE the ONE transport budget the header
        // read already started — it must never begin a second one. Asserted STRUCTURALLY, not on the wall
        // clock: an elapsed-time ceiling cannot tell a loaded CI runner from the defect returning, because
        // a stalled runner overshoots the bug's own signature (issue #330).
        //
        // The handler EXPIRES the SDK's per-call transport budget ITSELF — it fires the budget's armed
        // timer on a manual clock injected into the client, and the cancellation chain runs synchronously
        // inside that call, so the token the SDK handed the transport is observed as already-fired before
        // the headers are delivered. The ordering is an event the handler causes, never a wall-clock race:
        // a starved runner can delay real timers past any fixed guard (issue #343), which is how the old
        // wait-on-the-token-vs-30s-guard handshake was occasionally observed as un-expired on net8.0.
        // Only then does it deliver a 409 whose body is legible only to a reader that still holds budget:
        //   ONE shared budget -> the error-body read is born already-cancelled, so classification falls
        //                        back to the status alone: Conflict, no error_code.
        //   a SECOND budget   -> the body parses and its error_code (path_not_found) reclassifies the
        //                        failure as NotFound — which is exactly what this test refuses.
        const string errorBody = """{"error":"gone","code":409,"error_code":"path_not_found","retryable":false}""";
        var serverAddress = TestSupport.NewLoopbackAddress();
        var clock = new ManualTimeProvider();
        var handler = new BudgetExhaustingHeaderHandler(errorBody, clock);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = serverAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var options = new SandboxClientOptions(
            serverAddress,
            "app-1",
            TestSupport.ValidSecret,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(200)
        );
        using var client = new SandboxClient(options, httpClient) { TransportClock = clock };

        Func<Task> act = () => client.ReadTextFileAsync(Session, "notes.txt");
        var exception = await act.Should().ThrowAsync<SandboxException>();

        // Non-vacuity first: if the header phase had NOT exhausted the budget, everything below would be
        // meaningless, so fail loudly on that rather than silently proving nothing.
        handler
            .BudgetExpiredBeforeHeaders.Should()
            .BeTrue("the header read must consume the entire transport budget before the error body is offered");

        // Status-only classification: the shared budget was already spent, so the error body was never
        // read. A fresh second TransportTimeout would have read it and produced NotFound/path_not_found.
        exception.Which.Kind.Should().Be(SandboxErrorKind.Conflict);
        exception.Which.ErrorCode.Should().BeNull();
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
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new OversizedContent(SandboxClient.MaxDirectReadBytes + 1),
            }
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
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnsizedStreamContent(SandboxClient.MaxDirectReadBytes + 1),
            }
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
        public NeverEndingContent() =>
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new NeverEndingStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.Infinite);

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
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Answers mount resolution immediately; on the files GET it EXPIRES the SDK's per-call transport
    /// budget itself — firing the budget's armed timer on the test's <see cref="ManualTimeProvider"/> —
    /// and then observes the very <see cref="CancellationToken"/> the SDK handed the transport. The
    /// cancellation chain (budget CTS → the SDK's linked per-call CTS → this handler's token) runs
    /// synchronously inside that call, so the token is seen as fired-before-headers iff the SDK really
    /// armed its budget through the clock — an event this handler CAUSES, never a wall-clock deadline it
    /// races. (The previous handshake waited on the token against a 30s guard; a starved runner could
    /// process the guard's timer before the budget's 200ms timer and observe the budget as un-expired —
    /// issues #330/#343.) It then returns a non-2xx carrying a <see cref="BudgetGatedContent"/> error
    /// body, which makes "did the error-body read start a SECOND transport budget?" visible in the
    /// classified exception instead of in elapsed milliseconds.
    /// </summary>
    private sealed class BudgetExhaustingHeaderHandler(string errorBody, ManualTimeProvider clock) : HttpMessageHandler
    {
        /// <summary>True only if the transport token fired BEFORE the file-GET headers were delivered — the precondition every assertion here rests on.</summary>
        public bool BudgetExpiredBeforeHeaders { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/files/", StringComparison.Ordinal))
            {
                // Fire the SDK's armed transport-budget timer now, then observe the per-call token. Both
                // steps are synchronous, so a false observation can only mean the SDK never armed the
                // budget (or armed it off-clock) — never that a real timer was still in flight.
                _ = clock.FireArmed();
                BudgetExpiredBeforeHeaders = cancellationToken.IsCancellationRequested;

                // Deliver the headers anyway: a real gateway's response can land at the very moment the
                // client-side deadline lapses, and the SDK must still classify it.
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new BudgetGatedContent(errorBody) }
                );
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"session_id":"s1","volumes":{"workspace":{"container_path":"/workspace","read_only":false,"id":7}}}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
            );
        }
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> whose timers fire ONLY when the test says so: time never advances on
    /// its own. <see cref="FireArmed"/> synchronously invokes every armed, un-disposed timer callback on
    /// the calling thread, so a <see cref="CancellationTokenSource"/> built on this clock cancels — and
    /// runs its whole linked-token chain — inside that call. This is what lets
    /// <see cref="BudgetExhaustingHeaderHandler"/> turn "the transport budget expired" from a raced
    /// ThreadPool timer into an event it causes and immediately observes.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        /// <summary>Synchronously fires every armed, un-disposed timer; returns how many fired.</summary>
        public int FireArmed()
        {
            List<ManualTimer> armed;
            lock (_gate)
            {
                armed = [.. _timers.Where(t => t.IsArmed)];
            }

            foreach (var timer in armed)
            {
                timer.Fire();
            }

            return armed.Count;
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private readonly object _gate = new();
            private TimeSpan _dueTime = dueTime;
            private bool _disposed;

            public bool IsArmed
            {
                get
                {
                    lock (_gate)
                    {
                        return !_disposed && _dueTime != Timeout.InfiniteTimeSpan;
                    }
                }
            }

            public void Fire()
            {
                lock (_gate)
                {
                    if (_disposed || _dueTime == Timeout.InfiniteTimeSpan)
                    {
                        return;
                    }

                    // One-shot: disarm before invoking so a re-entrant Change/Dispose from the callback
                    // (CancellationTokenSource disposes its timer while cancelling) cannot double-fire.
                    _dueTime = Timeout.InfiniteTimeSpan;
                }

                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    _dueTime = dueTime;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    _disposed = true;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// An error body legible ONLY to a reader that still holds transport budget: its read stream throws
    /// <see cref="OperationCanceledException"/> the instant it is asked for bytes under an already-cancelled
    /// token, and otherwise yields the JSON in full. Reading it therefore proves a SECOND budget was armed;
    /// failing to read it proves the one shared budget was already spent.
    /// </summary>
    private sealed class BudgetGatedContent : HttpContent
    {
        private readonly byte[] _bytes;

        public BudgetGatedContent(string json)
        {
            _bytes = Encoding.UTF8.GetBytes(json);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new BudgetGatedStream(_bytes));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }
    }

    /// <summary>A read stream that refuses to produce a single byte under an already-cancelled token, and otherwise replays <paramref name="bytes"/>.</summary>
    private sealed class BudgetGatedStream(byte[] bytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var produced = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsSpan(_position, produced).CopyTo(buffer.Span);
            _position += produced;
            return ValueTask.FromResult(produced);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ZeroStream(_length));

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
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

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
