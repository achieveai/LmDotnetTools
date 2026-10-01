using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.FileBrowser;
using LmStreaming.Sample.SandboxApps;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.SandboxApps;

public sealed class BrowserDebugMiddlewareTests
{
    [Fact]
    public async Task HtmlAndRelativeAssets_UseExistingFilePolicy_AndNeverFallThrough()
    {
        var fixture = new Fixture();
        var page = fixture.Request("index.html");
        await fixture.Middleware.InvokeAsync(page);
        page.Response.StatusCode.Should().Be(200);
        page.Response.ContentType.Should().Be("text/html; charset=utf-8");
        page.Response.Headers.ContentSecurityPolicy.ToString().Should().Be(WorkspaceContentTypes.SandboxPolicy);
        page.Response.Headers.ContentSecurityPolicy.ToString().Should().NotContain("allow-same-origin");
        page.Response.Headers.XContentTypeOptions.ToString().Should().Be("nosniff");
        Encoding.UTF8.GetString(((MemoryStream)page.Response.Body).ToArray()).Should().Be("<h1>Report</h1>");
        var css = fixture.Request("style.css");
        await fixture.Middleware.InvokeAsync(css);
        css.Response.ContentType.Should().Be("text/css; charset=utf-8");
        fixture.Fallthrough.Should().Be(0);
        fixture.Files.ReadCalls.Should().Be(0, "the path-based REST reader cannot enforce a narrower root atomically");
        fixture.Files.Commands.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(44, 404)]
    [InlineData(45, 413)]
    [InlineData(127, 502)]
    public async Task AssetReadFailure_RejectsPartialBytesWithoutFallingBack(int exitCode, int status)
    {
        var fixture = new Fixture();
        fixture.Files.BinaryExecResult = fixture.Files.BinaryExecResult with
        {
            ExitCode = exitCode,
            StandardOutput = "partial invalid bytes"u8.ToArray(),
        };
        var request = fixture.Request("index.html");
        await fixture.Middleware.InvokeAsync(request);
        request.Response.StatusCode.Should().Be(status);
        ((MemoryStream)request.Response.Body).Length.Should().Be(0);
        fixture.Files.ReadCalls.Should().Be(0);
        fixture.Files.Commands.Should().ContainSingle();
    }

    [Theory]
    [InlineData("link.html", 404)]
    [InlineData(".private/secret.js", 404)]
    [InlineData("huge.html", 413)]
    public async Task UnavailableAsset_RefusesBeforeReading(string path, int status)
    {
        var fixture = new Fixture();
        var request = fixture.Request(path);
        await fixture.Middleware.InvokeAsync(request);
        request.Response.StatusCode.Should().Be(status);
        fixture.Files.ReadCalls.Should().Be(0);
        fixture.Fallthrough.Should().Be(0);
    }

    [Fact]
    public async Task SignedBrowserTrafficToChatApi_IsRejected_WhileOrdinaryS2SIsNotIntercepted()
    {
        var fixture = new Fixture();
        var request = fixture.Request("index.html");
        request.Request.Path = "/api/conversations";
        fixture.Sign(request);
        await fixture.Middleware.InvokeAsync(request);
        request.Response.StatusCode.Should().Be(403);
        fixture.Fallthrough.Should().Be(0);

        var ordinary = new DefaultHttpContext();
        ordinary.Request.Path = "/api/conversations";
        ordinary.Request.Headers["X-Sbx-App-Id"] = "some-service";
        await fixture.Middleware.InvokeAsync(ordinary);
        fixture.Fallthrough.Should().Be(1);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("permission")]
    [InlineData("workspace")]
    public async Task ChangedAccess_RevokesPreviewBeforeAnyFileRead(string change)
    {
        var fixture = new Fixture();
        switch (change)
        {
            case "mode":
                fixture.GroupEnabled = false;
                break;
            case "permission":
                fixture.Metadata = fixture.Metadata with { OwnerUserId = "other" };
                break;
            case "workspace":
                fixture.Metadata = fixture.Metadata with
                {
                    Properties = fixture.Metadata.Properties!.SetItem(MultiTurnAgentPool.WorkspacePropertyKey, "other"),
                };
                break;
            default:
                throw new ArgumentException("Unknown change.", nameof(change));
        }
        var request = fixture.Request("index.html");
        await fixture.Middleware.InvokeAsync(request);
        request.Response.StatusCode.Should().BeOneOf(403, 404, 409);
        fixture.Files.ReadCalls.Should().Be(0);
        fixture
            .Bindings.TryGetOwned(fixture.Binding.Handle, "thread", "workspace", "session", out _)
            .Should()
            .BeFalse();
        fixture.Fallthrough.Should().Be(0);
    }

    private sealed class Fixture
    {
        private const string Secret = "test-browser-route-signing-secret-32-bytes";
        private static readonly Principal User = new()
        {
            TenantId = "tenant",
            Actor = new(PrincipalKind.EndUser, "owner"),
            Source = PrincipalSource.Interactive,
        };
        public bool GroupEnabled { get; set; } = true;
        public int Fallthrough { get; private set; }
        public FakeFileBrowser Files { get; }
        public BrowserDebugBindingStore Bindings { get; }
        public BrowserDebugBinding Binding { get; }
        public BrowserDebugMiddleware Middleware { get; }
        public ThreadMetadata Metadata { get; set; } =
            new()
            {
                ThreadId = "thread",
                LastUpdated = 0,
                TenantId = "tenant",
                OwnerUserId = "owner",
                Properties = ImmutableDictionary<string, object>
                    .Empty.Add(MultiTurnAgentPool.WorkspacePropertyKey, "workspace")
                    .Add(MultiTurnAgentPool.ModePropertyKey, "builder"),
            };

        public Fixture()
        {
            Files = new()
            {
                Resolution = new(
                    SandboxSessionResolutionOutcome.Resolved,
                    new("workspace", "session", "leaf", "/host/leaf"),
                    null,
                    null
                ),
                FileBytes = "<h1>Report</h1>"u8.ToArray(),
                BinaryExecResult = new()
                {
                    ExitCode = 0,
                    StandardOutput = "<h1>Report</h1>"u8.ToArray(),
                    StandardError = [],
                    OperationId = "read",
                },
            };
            Files.Listings[""] = [new("reports", SandboxEntryType.Directory, null, false)];
            Files.Listings["reports"] =
            [
                new("index.html", SandboxEntryType.File, 15, false),
                new("style.css", SandboxEntryType.File, 15, false),
                new("link.html", SandboxEntryType.Symlink, 1, false),
                new(".private", SandboxEntryType.Directory, null, false),
                new("huge.html", SandboxEntryType.File, FileBrowserLimits.MaxDownloadBytes + 1, false),
            ];
            Files.Listings["reports/.private"] = [new("secret.js", SandboxEntryType.File, 1, false)];
            var conversations = new Mock<IConversationStore>();
            conversations
                .Setup(x => x.LoadMetadataAsync("thread", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Metadata);
            var modes = new Mock<IChatModeStore>();
            modes
                .Setup(x => x.GetModeAsync("builder", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                    new ChatMode
                    {
                        Id = "builder",
                        Name = "Builder",
                        SystemPrompt = "debug",
                        EnabledCapabilityTools = GroupEnabled ? ["browser-debug:*"] : [],
                    }
                );
            var access = new BrowserDebugAccess(
                new(conversations.Object, Files, TestAuthorizers.Enforcing(User)),
                conversations.Object,
                modes.Object
            );
            Bindings = new(
                new()
                {
                    Enabled = true,
                    PreviewOrigin = "https://preview.invalid",
                    GatewayAppId = "sample",
                    SigningSecret = Secret,
                },
                TimeProvider.System
            );
            Binding = Bindings.Create(
                "thread",
                "workspace",
                "session",
                User,
                new(null, "reports/index.html", "reports")
            );
            Bindings.BindInstance(Binding, Binding.BrowserId, "instance");
            Middleware = new(
                _ =>
                {
                    Fallthrough++;
                    return Task.CompletedTask;
                },
                Bindings,
                access,
                Files,
                SandboxAppCatalog.Load(new ConfigurationBuilder().Build()),
                NullLogger<BrowserDebugMiddleware>.Instance
            );
        }

        public DefaultHttpContext Request(string path)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = Binding.Prefix + path;
            context.Request.Headers["X-Sbx-App-Id"] = "sample";
            context.Request.Headers["X-Sbx-Browser-Id"] = Binding.BrowserId;
            context.Request.Headers["X-Sbx-Browser-Instance"] = "instance";
            context.Response.Body = new MemoryStream();
            Sign(context);
            return context;
        }

        public void Sign(HttpContext context)
        {
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var original = "https://preview.invalid" + context.Request.Path + context.Request.QueryString;
            context.Request.Headers["X-Sbx-Original-Url"] = original;
            var message = $"v1\n{time}\n{context.Request.Method}\n{original}\nsample\n{Binding.BrowserId}\ninstance";
            var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(message));
            context.Request.Headers["X-Sbx-Browser-Signature"] =
                $"t={time},v1={Convert.ToHexString(digest).ToLowerInvariant()}";
        }
    }
}
