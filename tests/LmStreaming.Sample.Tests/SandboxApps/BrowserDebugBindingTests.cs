using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LmStreaming.Sample.SandboxApps;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.SandboxApps;

public sealed class BrowserDebugBindingTests
{
    private const string Secret = "test-signing-secret-with-at-least-32-bytes";
    private readonly Clock _clock = new();

    [Fact]
    public void SignedRequest_AllowsOnlyItsLiveTarget_AndCloseRevokesIt()
    {
        var store = Store();
        var binding = Create(store);
        store.BindInstance(binding, binding.BrowserId, "instance-1").Should().BeTrue();
        var request = Request(binding, binding.Prefix + "shared/chart.js", "?v=2");

        store.TryValidate(request, out var resolved, out var path).Should().BeTrue();
        resolved.Should().BeSameAs(binding);
        path.Should().Be("shared/chart.js");
        store.TryGetOwned(binding.Handle, "other-thread", "workspace", "session", out _).Should().BeFalse();
        store.TryGetOwned(binding.Handle, "thread", "other-workspace", "session", out _).Should().BeFalse();
        store.TryGetOwned(binding.Handle, "thread", "workspace", "other-session", out _).Should().BeFalse();
        store.Revoke(binding);
        store.TryValidate(request, out _, out _).Should().BeFalse();
        store.TryGetOwned(binding.Handle, "thread", "workspace", "session", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("path")]
    [InlineData("method")]
    [InlineData("query")]
    [InlineData("browser")]
    [InlineData("instance")]
    [InlineData("app")]
    [InlineData("signature")]
    [InlineData("duplicate-header")]
    [InlineData("stale")]
    [InlineData("future")]
    public void SignedRequest_RejectsTamperingAndOffTargetRequests(string fault)
    {
        var store = Store();
        var binding = Create(store);
        store.BindInstance(binding, binding.BrowserId, "instance-1");
        var request = Request(binding, binding.Prefix + "index.html");
        switch (fault)
        {
            case "origin":
                request.Headers["X-Sbx-Original-Url"] = "https://other.example" + request.Path;
                Sign(request);
                break;
            case "path":
                request.Path = "/api/conversations";
                request.Headers["X-Sbx-Original-Url"] = "https://preview.invalid/api/conversations";
                Sign(request);
                break;
            case "method":
                request.Method = "POST";
                break;
            case "query":
                request.QueryString = new QueryString("?tampered=1");
                break;
            case "browser":
                request.Headers["X-Sbx-Browser-Id"] = "other-browser";
                Sign(request);
                break;
            case "instance":
                request.Headers["X-Sbx-Browser-Instance"] = "instance-2";
                Sign(request);
                break;
            case "app":
                request.Headers["X-Sbx-App-Id"] = "other-app";
                Sign(request);
                break;
            case "signature":
                request.Headers["X-Sbx-Browser-Signature"] = "t=0,v1=" + new string('0', 64);
                break;
            case "duplicate-header":
                request.Headers.Append("X-Sbx-Browser-Id", binding.BrowserId);
                break;
            case "stale":
                Sign(request, _clock.GetUtcNow().ToUnixTimeSeconds() - 301);
                break;
            case "future":
                Sign(request, _clock.GetUtcNow().ToUnixTimeSeconds() + 301);
                break;
            default:
                throw new ArgumentException("Unknown test fault.", nameof(fault));
        }
        store.TryValidate(request, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("../private.txt")]
    [InlineData("%2e%2e/private.txt")]
    [InlineData("%252e%252e/private.txt")]
    [InlineData("shared%2f..%2fprivate.txt")]
    [InlineData("shared%5cprivate.txt")]
    [InlineData("shared/%00bad")]
    public void SignedRequest_RejectsTraversalBeforeUriNormalization(string tail)
    {
        var store = Store();
        var binding = Create(store);
        store.BindInstance(binding, binding.BrowserId, "instance-1");
        var request = Request(binding, binding.Prefix + tail);

        store.TryValidate(request, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void ReplacementOrExpiry_RequiresAFreshHandleAndCannotServeOldEvidence()
    {
        var store = Store();
        var binding = Create(store);
        store.BindInstance(binding, binding.BrowserId, "instance-1").Should().BeTrue();
        store.BindInstance(binding, binding.BrowserId, "instance-2").Should().BeFalse();
        store.TryGetOwned(binding.Handle, "thread", "workspace", "session", out _).Should().BeFalse();

        var reopened = Create(store);
        reopened.Handle.Should().NotBe(binding.Handle);
        reopened.BrowserId.Should().NotBe(binding.BrowserId);
        store.BindInstance(reopened, reopened.BrowserId, "fresh-instance").Should().BeTrue();
        _clock.Now += TimeSpan.FromMinutes(31);
        store.TryValidate(Request(reopened, reopened.Prefix), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void BrowserDiscoveryMustBindTheExpectedIdBeforeAnyPageCanLoad()
    {
        var store = Store();
        var binding = Create(store);
        store.TryValidate(Request(binding, binding.Prefix), out _, out _).Should().BeFalse();
        store.BindInstance(binding, "wrong-browser", "instance-1").Should().BeFalse();
        store.TryGetOwned(binding.Handle, "thread", "workspace", "session", out _).Should().BeFalse();
    }

    private BrowserDebugBindingStore Store() =>
        new(
            new BrowserDebugOptions
            {
                Enabled = true,
                PreviewOrigin = "https://preview.invalid",
                GatewayAppId = "sample",
                SigningSecret = Secret,
            },
            _clock
        );

    private static BrowserDebugBinding Create(BrowserDebugBindingStore store) =>
        store.Create("thread", "workspace", "session", null, new BrowserDebugTarget(null, "reports/index.html", ""));

    private HttpRequest Request(BrowserDebugBinding binding, string path, string query = "")
    {
        var request = new DefaultHttpContext().Request;
        request.Method = "GET";
        request.Path = Uri.UnescapeDataString(path);
        request.QueryString = new QueryString(query);
        request.Headers["X-Sbx-Original-Url"] = "https://preview.invalid" + path + query;
        request.Headers["X-Sbx-App-Id"] = "sample";
        request.Headers["X-Sbx-Browser-Id"] = binding.BrowserId;
        request.Headers["X-Sbx-Browser-Instance"] = binding.BrowserInstance ?? "instance-1";
        Sign(request);
        return request;
    }

    private void Sign(HttpRequest request, long? timestamp = null)
    {
        var time = (timestamp ?? _clock.GetUtcNow().ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture);
        // This is the gateway's documented wire format; no production signing helper is used.
        var message =
            $"v1\n{time}\n{request.Method}\n{request.Headers["X-Sbx-Original-Url"]}\n{request.Headers["X-Sbx-App-Id"]}\n{request.Headers["X-Sbx-Browser-Id"]}\n{request.Headers["X-Sbx-Browser-Instance"]}";
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(message));
        request.Headers["X-Sbx-Browser-Signature"] = $"t={time},v1={Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 20, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
