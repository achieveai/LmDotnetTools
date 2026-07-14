using System.Net;
using System.Text;
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
}
