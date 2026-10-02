using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.SandboxApps;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.SandboxApps;

public sealed class BrowserDebugToolProviderTests
{
    [Fact]
    public async Task OptionalArguments_AcceptNullSoModelsCanOmitTheUnusedTarget()
    {
        await using var fixture = new Fixture();
        foreach (var name in BrowserDebugToolProvider.ToolNames)
        {
            foreach (var parameter in fixture.Function(name).Contract.Parameters!)
            {
                JsonSchemaTypeHelper
                    .IsNullable(parameter.ParameterType!)
                    .Should()
                    .Be(
                        !parameter.IsRequired,
                        $"{name}.{parameter.Name} must represent omission without an empty string"
                    );
            }
        }
        var opened = await fixture.Call(
            "OpenDebugBrowser",
            """{"miniAppId":null,"htmlFilePath":"reports/index.html","assetsRootPath":null}"""
        );
        opened.Payload.IsError.Should().BeFalse();
        using var openedJson = JsonDocument.Parse(opened.Payload.Text);
        var discovered = await fixture.Call(
            "RunBrowserTool",
            JsonSerializer.Serialize(
                new
                {
                    browserHandle = openedJson.RootElement.GetProperty("browserHandle").GetString(),
                    toolkit = "playwright",
                    toolName = (string?)null,
                    toolArguments = (object?)null,
                }
            )
        );
        discovered.Payload.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task ActionArgumentsSchema_AllowsFieldsDiscoveredFromTheGateway()
    {
        await using var fixture = new Fixture();
        var argument = fixture.Function("RunBrowserTool").Contract.Parameters!.Single(p => p.Name == "toolArguments");
        argument
            .ParameterType!.AdditionalProperties.Should()
            .BeTrue("upstream actions have runtime-discovered argument fields");
    }

    [Fact]
    public async Task OpenHtml_BindsBeforeNavigation_AndReturnsHandlePageAndImage()
    {
        await using var fixture = new Fixture();
        var opened = await fixture.Call("OpenDebugBrowser", """{"htmlFilePath":"reports/index.html"}""");

        opened.Payload.IsError.Should().BeFalse();
        using var json = JsonDocument.Parse(opened.Payload.Text);
        json.RootElement.GetProperty("browserHandle").GetString().Should().HaveLength(64);
        json.RootElement.GetProperty("previewUrl").GetString().Should().EndWith("/index.html");
        json.RootElement.GetProperty("browserInstance").GetString().Should().Be("instance-1");
        opened.Payload.ContentBlocks!.OfType<ImageToolResultBlock>().Should().ContainSingle();
        fixture.Gateway.Calls.Select(c => c.Tool).Should().Equal(null, "browser_navigate", "browser_take_screenshot");
        fixture.Gateway.BoundBeforeNavigate.Should().BeTrue();
        fixture.Gateway.Calls.Select(c => c.Id).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task RunAndClose_CannotOverrideTheOwnedLease_AndReopenIsFresh()
    {
        await using var fixture = new Fixture();
        var handle = await fixture.Open();
        var id = fixture.Gateway.Calls[0].Id;
        var result = await fixture.Call(
            "RunBrowserTool",
            JsonSerializer.Serialize(
                new
                {
                    browserHandle = handle,
                    toolkit = "devtools",
                    toolName = "list_network_requests",
                    toolArguments = new { browser_id = "foreign-browser" },
                }
            )
        );
        result.Payload.IsError.Should().BeFalse();
        fixture.Gateway.Calls[^1].Id.Should().Be(id);
        fixture.Gateway.Calls[^1].Toolkit.Should().Be("devtools");
        var close = await fixture.Call("CloseDebugBrowser", JsonSerializer.Serialize(new { browserHandle = handle }));
        close.Payload.IsError.Should().BeFalse();
        fixture.Gateway.Closed.Should().Equal(id);
        fixture.Bindings.TryGetOwned(handle, "thread", "workspace", "session", out _).Should().BeFalse();
        (
            await fixture.Call(
                "RunBrowserTool",
                JsonSerializer.Serialize(new { browserHandle = handle, toolkit = "playwright" })
            )
        )
            .Payload.ErrorCode.Should()
            .Be("browser_not_found");
        var reopened = await fixture.Open();
        reopened.Should().NotBe(handle);
        fixture.Gateway.Calls[^1].Id.Should().NotBe(id);
    }

    [Fact]
    public async Task DisablingMode_RefusesExistingHandlesBeforeBrowserExecution()
    {
        await using var fixture = new Fixture();
        var handle = await fixture.Open();
        var previousCalls = fixture.Gateway.Calls.Count;
        fixture.GroupEnabled = false;

        var result = await fixture.Call(
            "RunBrowserTool",
            JsonSerializer.Serialize(new { browserHandle = handle, toolkit = "playwright" })
        );
        result.Payload.ErrorCode.Should().Be("browser_access_denied");
        fixture.Gateway.Calls.Count.Should().Be(previousCalls);
        (await fixture.Access.ResolveAsync("thread", null, null, CancellationToken.None)).Status.Should().Be(403);
    }

    [Fact]
    public async Task GatewayError_PreservesImageAndErrorFlag_WhileReplacementRevokesHandle()
    {
        await using var fixture = new Fixture();
        var handle = await fixture.Open();
        fixture.Gateway.Error = true;
        var args = JsonSerializer.Serialize(
            new
            {
                browserHandle = handle,
                toolkit = "playwright",
                toolName = "browser_snapshot",
            }
        );
        var error = await fixture.Call("RunBrowserTool", args);
        error.Payload.IsError.Should().BeTrue();
        error.Payload.ContentBlocks!.OfType<ImageToolResultBlock>().Should().ContainSingle();
        error.Payload.Text.Should().Contain("page failure");
        fixture.Gateway.Error = false;
        fixture.Gateway.Instance = "replacement";
        (await fixture.Call("RunBrowserTool", args)).Payload.ErrorCode.Should().Be("browser_replaced");
        fixture.Bindings.TryGetOwned(handle, "thread", "workspace", "session", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingGatewayIdentity_RejectsUnboundEvidenceAndClosesLease(bool isError)
    {
        await using var fixture = new Fixture();
        var handle = await fixture.Open();
        var id = fixture.Gateway.Calls[0].Id;
        fixture.Gateway.Error = isError;
        fixture.Gateway.OmitMetadata = true;

        var result = await fixture.Call(
            "RunBrowserTool",
            JsonSerializer.Serialize(
                new
                {
                    browserHandle = handle,
                    toolkit = "playwright",
                    toolName = "browser_snapshot",
                }
            )
        );

        result.Payload.ErrorCode.Should().Be("browser_replaced");
        result.Payload.Text.Should().NotContain("page failure").And.NotContain("page snapshot");
        result.Payload.ContentBlocks?.OfType<ImageToolResultBlock>().Should().BeNullOrEmpty();
        fixture.Bindings.TryGetOwned(handle, "thread", "workspace", "session", out _).Should().BeFalse();
        fixture.Gateway.Closed.Should().Contain(id);
    }

    [Fact]
    public async Task FailedClose_RevokesThePreviewButKeepsTheLease_SoRetryReleasesTheSameBrowser()
    {
        await using var fixture = new Fixture();
        var handle = await fixture.Open();
        var id = fixture.Gateway.Calls[0].Id;
        fixture.Gateway.FailCloses = 1;
        var args = JsonSerializer.Serialize(new { browserHandle = handle });

        (await fixture.Call("CloseDebugBrowser", args)).Payload.ErrorCode.Should().Be("browser_unavailable");
        fixture.Bindings.TryGetOwned(handle, "thread", "workspace", "session", out _).Should().BeFalse();

        (await fixture.Call("CloseDebugBrowser", args)).Payload.IsError.Should().BeFalse();
        fixture.Gateway.CloseAttempts.Should().Equal(id, id);
        (await fixture.Call("CloseDebugBrowser", args)).Payload.ErrorCode.Should().Be("browser_not_found");
    }

    [Fact]
    public async Task FailedClose_IsReleasedAgainWhenTheProviderIsDisposed()
    {
        var fixture = new Fixture();
        var handle = await fixture.Open();
        var id = fixture.Gateway.Calls[0].Id;
        fixture.Gateway.FailCloses = 1;
        (await fixture.Call("CloseDebugBrowser", JsonSerializer.Serialize(new { browserHandle = handle })))
            .Payload.ErrorCode.Should()
            .Be("browser_unavailable");

        await fixture.DisposeAsync();

        fixture.Gateway.CloseAttempts.Should().Equal(id, id);
    }

    [Fact]
    public async Task ExpiredPreviews_AreReleasedBeforeTheCapacityCheck()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = new Fixture(clock);
        for (var i = 0; i < 8; i++)
        {
            await fixture.Open();
        }
        var expired = fixture.Gateway.Calls.Select(c => c.Id).Distinct().ToArray();
        (await fixture.Call("OpenDebugBrowser", """{"htmlFilePath":"reports/index.html"}"""))
            .Payload.ErrorCode.Should()
            .Be("browser_limit");

        clock.Advance(TimeSpan.FromMinutes(31));

        await fixture.Open();
        fixture.Gateway.Closed.Should().BeEquivalentTo(expired);
    }

    [Theory]
    [InlineData("{}", "invalid_args")]
    [InlineData("{\"miniAppId\":\"demo\",\"htmlFilePath\":\"reports/index.html\"}", "invalid_args")]
    [InlineData("{\"htmlFilePath\":\"reports/index.html\",\"assetsRootPath\":\"../private\"}", "invalid_args")]
    [InlineData("{\"htmlFilePath\":\"reports/link.html\"}", "file_not_found")]
    public async Task InvalidOrSymlinkTarget_NeverLeasesABrowser(string args, string code)
    {
        await using var fixture = new Fixture();
        var result = await fixture.Call("OpenDebugBrowser", args);
        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be(code);
        fixture.Gateway.Calls.Should().BeEmpty();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public bool GroupEnabled { get; set; } = true;
        public BrowserDebugBindingStore Bindings { get; }
        public BrowserDebugAccess Access { get; }
        public Gateway Gateway { get; }
        private readonly BrowserDebugToolProvider _provider;

        public Fixture(TimeProvider? clock = null)
        {
            var files = new FakeFileBrowser
            {
                Resolution = new(
                    SandboxSessionResolutionOutcome.Resolved,
                    new("workspace", "session", "leaf", "/host/leaf"),
                    null,
                    null
                ),
            };
            files.Listings[""] = [new("reports", SandboxEntryType.Directory, null, false)];
            files.Listings["reports"] =
            [
                new("index.html", SandboxEntryType.File, 64, false),
                new("link.html", SandboxEntryType.Symlink, 1, false),
            ];
            var conversations = new Mock<IConversationStore>();
            conversations
                .Setup(x => x.LoadMetadataAsync("thread", It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                    new ThreadMetadata
                    {
                        ThreadId = "thread",
                        LastUpdated = 0,
                        Properties = ImmutableDictionary<string, object>
                            .Empty.Add(MultiTurnAgentPool.WorkspacePropertyKey, "workspace")
                            .Add(MultiTurnAgentPool.ModePropertyKey, "builder"),
                    }
                );
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
            Access = new(
                new(conversations.Object, files, TestAuthorizers.Disabled()),
                conversations.Object,
                modes.Object
            );
            Bindings = new(
                new()
                {
                    Enabled = true,
                    PreviewOrigin = "https://preview.invalid",
                    GatewayAppId = "sample",
                    SigningSecret = new string('k', 32),
                },
                clock ?? TimeProvider.System
            );
            Gateway = new(Bindings);
            _provider = new(Bindings, Access, files, new(files), Gateway, "thread", "workspace", "session", null);
        }

        public async Task<string> Open()
        {
            var result = await Call(
                "OpenDebugBrowser",
                """{"htmlFilePath":"reports/index.html","assetsRootPath":"."}"""
            );
            result.Payload.IsError.Should().BeFalse();
            using var json = JsonDocument.Parse(result.Payload.Text);
            json.RootElement.GetProperty("previewUrl").GetString().Should().EndWith("/reports/index.html");
            return json.RootElement.GetProperty("browserHandle").GetString()!;
        }

        public FunctionDescriptor Function(string name) =>
            _provider.GetFunctions().Single(f => f.Contract.Name == name);

        public async Task<ToolHandlerResult.Resolved> Call(string name, string args) =>
            Assert.IsType<ToolHandlerResult.Resolved>(
                await Function(name).Handler(args, new ToolCallContext(), CancellationToken.None)
            );

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    private sealed class Gateway(BrowserDebugBindingStore bindings) : IBrowserDebugGateway
    {
        public List<(string Id, string Toolkit, string? Tool)> Calls { get; } = [];
        public List<string> Closed { get; } = [];
        public List<string> CloseAttempts { get; } = [];
        public int FailCloses { get; set; }
        public string Instance { get; set; } = "instance-1";
        public bool Error { get; set; }
        public bool OmitMetadata { get; set; }
        public bool BoundBeforeNavigate { get; private set; }

        public Task<bool> SupportsBrowserAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<JsonElement> CallAsync(
            string browserId,
            string toolkit,
            string? toolName,
            JsonElement? arguments,
            CancellationToken ct
        )
        {
            Calls.Add((browserId, toolkit, toolName));
            if (toolName == "browser_navigate")
            {
                var url = arguments!.Value.GetProperty("url").GetString()!;
                var handle = new Uri(url).AbsolutePath.Split('/')[2];
                BoundBeforeNavigate =
                    bindings.TryGetOwned(handle, "thread", "workspace", "session", out var binding)
                    && binding!.BrowserInstance == Instance;
            }
            var response = JsonSerializer
                .SerializeToNode(
                    new
                    {
                        content = (toolName == "browser_take_screenshot" || Error)
                            ? new object[]
                            {
                                new { type = "text", text = Error ? "page failure" : "page snapshot" },
                                new
                                {
                                    type = "image",
                                    data = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==",
                                    mimeType = "image/png",
                                },
                            }
                            : [new { type = "text", text = "page snapshot" }],
                        isError = Error,
                        _meta = new Dictionary<string, string>
                        {
                            ["sbx/browser_id"] = browserId,
                            ["sbx/browser_instance"] = Instance,
                        },
                    }
                )!
                .AsObject();
            if (OmitMetadata)
                response.Remove("_meta");
            return Task.FromResult(JsonSerializer.SerializeToElement(response));
        }

        public Task<bool> CloseAsync(string browserId, CancellationToken ct)
        {
            CloseAttempts.Add(browserId);
            if (FailCloses > 0)
            {
                FailCloses--;
                throw new SandboxException(SandboxErrorKind.TransportTimeout, "close timed out");
            }
            Closed.Add(browserId);
            return Task.FromResult(true);
        }
    }
}
