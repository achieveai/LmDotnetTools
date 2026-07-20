using System.Net;
using System.Text;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Transfer;

/// <summary>
/// Wire-level tests for the additive rich-listing and binary-safe byte APIs (WI #195):
/// <see cref="SandboxClient.ListDirectoryEntriesAsync"/> (name + kind + size + lossy),
/// <see cref="SandboxClient.ReadFileBytesAsync"/> (raw bytes, caller byte cap), and
/// <see cref="SandboxClient.WriteFileBytesAsync"/> (raw bytes, same one-shot parent self-heal). Each test
/// proves a genuine wire outcome through the in-memory <see cref="FakeGatewayHandler"/>.
/// </summary>
public class RichDirectoryAndBytesTests
{
    private const string Session = "s1";
    private const long MountId = 7;

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

    private static void OnListing(FakeGatewayHandler handler, string json) =>
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/directories/{MountId}", StringComparison.Ordinal),
            _ => JsonResponse(json)
        );

    // ---- ListDirectoryEntriesAsync ----

    [Fact]
    public async Task ListDirectoryEntriesAsync_MapsNameTypeSizeAndLossy()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        OnListing(
            handler,
            """
            {"entries":[
              {"name":"a.txt","type":"file","size":12,"name_lossy":false},
              {"name":"sub","type":"directory"},
              {"name":"link","type":"symlink","size":null},
              {"name":"weird","type":"file","size":3,"name_lossy":true}
            ]}
            """
        );

        var entries = await client.ListDirectoryEntriesAsync(Session, "");

        entries.Should().HaveCount(4);
        entries[0].Should().Be(new SandboxDirectoryEntry("a.txt", SandboxEntryType.File, 12, false));
        entries[1].Should().Be(new SandboxDirectoryEntry("sub", SandboxEntryType.Directory, null, false));
        entries[2].Should().Be(new SandboxDirectoryEntry("link", SandboxEntryType.Symlink, null, false));
        entries[3].Should().Be(new SandboxDirectoryEntry("weird", SandboxEntryType.File, 3, true));
    }

    [Fact]
    public async Task ListDirectoryEntriesAsync_AbsentNameLossy_DefaultsToFalse()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        OnListing(handler, """{"entries":[{"name":"a.txt","type":"file","size":1}]}""");

        var entries = await client.ListDirectoryEntriesAsync(Session, "");

        entries.Should().ContainSingle().Which.NameLossy.Should().BeFalse();
    }

    [Fact]
    public async Task ListDirectoryEntriesAsync_UnknownType_ThrowsProtocol()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        // An exotic/unknown kind must NOT be silently classified — the SDK never guesses a type.
        OnListing(handler, """{"entries":[{"name":"sock","type":"socket"}]}""");

        Func<Task> act = () => client.ListDirectoryEntriesAsync(Session, "");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ListDirectoryEntriesAsync_AbsentType_ThrowsProtocol()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        // No "type" field at all — System.Text.Json leaves Type null; an absent required type is Protocol.
        OnListing(handler, """{"entries":[{"name":"a.txt","size":1}]}""");

        Func<Task> act = () => client.ListDirectoryEntriesAsync(Session, "");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ListDirectoryEntriesAsync_NoName_ThrowsProtocol()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        OnListing(handler, """{"entries":[{"type":"file","size":1}]}""");

        Func<Task> act = () => client.ListDirectoryEntriesAsync(Session, "");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ListDirectoryEntriesAsync_Paginated_ConcatenatesPages_AndThreadsCursor()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        handler.On(
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/directories/{MountId}", StringComparison.Ordinal)
                && !req.RequestUri.Query.Contains("cursor=", StringComparison.Ordinal),
            _ => JsonResponse("""{"entries":[{"name":"a","type":"file","size":1}],"next_cursor":"c1"}""")
        );
        handler.On(
            req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath.EndsWith($"/directories/{MountId}", StringComparison.Ordinal)
                && req.RequestUri.Query.Contains("cursor=c1", StringComparison.Ordinal),
            _ => JsonResponse("""{"entries":[{"name":"b","type":"directory"}]}""")
        );

        var entries = await client.ListDirectoryEntriesAsync(Session, "");

        entries.Select(e => e.Name).Should().Equal("a", "b");
        entries.Select(e => e.Type).Should().Equal(SandboxEntryType.File, SandboxEntryType.Directory);
    }

    [Fact]
    public async Task ListDirectoryEntriesAsync_RepeatedCursor_ThrowsProtocol()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        OnListing(handler, """{"entries":[{"name":"a","type":"file"}],"next_cursor":"loop"}""");

        Func<Task> act = () => client.ListDirectoryEntriesAsync(Session, "");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ListDirectoryAsync_StillReturnsNames_SignatureUnchanged()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        OnListing(handler, """{"entries":[{"name":"a.txt","type":"file","size":1},{"name":"sub","type":"directory"}]}""");

        // The names-only projection is unchanged and the return type is still IReadOnlyList<string>
        // (existing consumers bind to it) — passing it to a typed local pins the contract at compile time.
        var names = await client.ListDirectoryAsync(Session, "");
        AcceptsStringList(names);

        names.Should().Equal("a.txt", "sub");
    }

    private static void AcceptsStringList(IReadOnlyList<string> names) => names.Should().NotBeNull();

    // ---- ReadFileBytesAsync ----

    [Fact]
    public async Task ReadFileBytesAsync_ReturnsRawBytes_WithoutUtf8Decode()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        // 0xFF/0xFE are never valid UTF-8 — ReadTextFileAsync would reject them (Integrity), but the raw
        // byte read must return them verbatim (this is how binary downloads work).
        var raw = new byte[] { 0xFF, 0xFE, 0x00, 0x80, 0x41 };
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) }
        );

        var bytes = await client.ReadFileBytesAsync(Session, "binary.bin");

        bytes.Should().Equal(raw);
    }

    [Fact]
    public async Task ReadFileBytesAsync_MaxBytesExactlyAtCap_Succeeds()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        var body = new byte[] { 1, 2, 3, 4 };
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
        );

        var bytes = await client.ReadFileBytesAsync(Session, "f.bin", maxBytes: 4);

        bytes.Should().Equal(body);
    }

    [Fact]
    public async Task ReadFileBytesAsync_DeclaredLengthOverMaxBytes_ThrowsProtocol()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        // ByteArrayContent declares an exact Content-Length; 5 declared bytes exceed the caller's cap of 4,
        // so it is refused by the declared length before buffering.
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4, 5]) }
        );

        Func<Task> act = () => client.ReadFileBytesAsync(Session, "f.bin", maxBytes: 4);

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ReadFileBytesAsync_StreamedPastMaxBytes_ThrowsProtocol()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        // No Content-Length (chunked): only the streaming counter can catch it. 8 bytes stream past the
        // caller's cap of 4.
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedZeroContent(8) }
        );

        Func<Task> act = () => client.ReadFileBytesAsync(Session, "f.bin", maxBytes: 4);

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ReadFileBytesAsync_DefaultCap_RefusesOver64MiBDeclared()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        // With no maxBytes the default 64 MiB ceiling applies; a declared 64 MiB + 1 is refused before
        // buffering (no bytes are ever produced).
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new DeclaredLengthContent(SandboxClient.MaxDirectReadBytes + 1) }
        );

        Func<Task> act = () => client.ReadFileBytesAsync(Session, "huge.bin");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ReadFileBytesAsync_MaxBytesAboveDefaultCeiling_StillClampedToDefault()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        // A caller asking for MORE than the 64 MiB ceiling cannot widen it: a declared 64 MiB + 1 is still
        // refused (the requested cap is clamped down to the default).
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new DeclaredLengthContent(SandboxClient.MaxDirectReadBytes + 1) }
        );

        Func<Task> act = () => client.ReadFileBytesAsync(Session, "huge.bin", maxBytes: long.MaxValue);

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    // ---- WriteFileBytesAsync ----

    [Fact]
    public async Task WriteFileBytesAsync_SendsExactBytes()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        byte[]? sent = null;
        handler.On(
            req => req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            req =>
            {
                sent = req.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return JsonResponse($$"""{"bytes_written":{{sent.Length}}}""");
            }
        );

        var payload = new byte[] { 0x00, 0xFF, 0x10, 0x20, 0x7F };
        await client.WriteFileBytesAsync(Session, "blob.bin", payload);

        sent.Should().Equal(payload);
    }

    [Fact]
    public async Task WriteFileBytesAsync_NestedParentMissing_MkdirsThenRetries()
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
                return JsonResponse($$"""{"bytes_written":{{stored.Length}}}""");
            }
        );
        handler.On(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal),
            req =>
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var opId = doc.RootElement.GetProperty("operation_id").GetString();
                return JsonResponse($$"""{"operation_id":"{{opId}}","status":"succeeded","exit_code":0}""");
            }
        );

        var payload = new byte[] { 9, 8, 7 };
        await client.WriteFileBytesAsync(Session, "nested/dir/blob.bin", payload);

        stored.Should().Equal(payload);
        putCount.Should().Be(2);
        handler
            .Requests.Should()
            .ContainSingle(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/operations", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteFileBytesAsync_TargetLocked_ThrowsConflict()
    {
        var (client, handler) = CreateClient();
        using var _ = client;
        handler.On(
            req => req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith($"/files/{MountId}", StringComparison.Ordinal),
            _ => JsonResponse(
                """{"error":"target locked","code":409,"error_code":"target_locked","retryable":true}""",
                HttpStatusCode.Conflict
            )
        );

        Func<Task> act = () => client.WriteFileBytesAsync(Session, "busy.bin", [1]);

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Conflict);
    }

    [Fact]
    public async Task WriteFileBytesAsync_WorkspaceRoot_ThrowsArgumentException()
    {
        var (client, _) = CreateClient();
        using var __ = client;

        Func<Task> act = () => client.WriteFileBytesAsync(Session, "", [1]);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>Declares a large <c>Content-Length</c> without allocating bytes — exercises the pre-read size guard.</summary>
    private sealed class DeclaredLengthContent : HttpContent
    {
        private readonly long _length;

        public DeclaredLengthContent(long length) => _length = length;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }

    /// <summary>Reports no <c>Content-Length</c> (chunked) and lazily yields a fixed number of zero bytes — exercises the streaming byte cap.</summary>
    private sealed class UnsizedZeroContent : HttpContent
    {
        private readonly long _length;

        public UnsizedZeroContent(long length) => _length = length;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => new ZeroStream(_length).CopyToAsync(stream);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ZeroStream(_length));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

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
