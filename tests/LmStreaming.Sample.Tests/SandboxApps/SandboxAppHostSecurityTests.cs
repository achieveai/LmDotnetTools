using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.SandboxApps;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.SandboxApps;

public sealed class SandboxAppHostSecurityTests
{
    private static readonly Principal User = new()
    {
        TenantId = "tenant",
        Actor = new PrincipalRef(PrincipalKind.EndUser, "user"),
        Source = PrincipalSource.Interactive,
    };
    private static SandboxAppDefinition DemoApp =>
        new("demo", "Demo", "/plugins/sandbox-apps/demo", [], null, 65536, 8388608, TimeSpan.FromSeconds(30));

    [Fact]
    public void Catalog_RequiresExplicitHttpsSameSiteConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["SandboxApps:Enabled"] = "true",
                    ["SandboxApps:SiteDomain"] = "example.test",
                    ["SandboxApps:AppDomain"] = "apps.example.test",
                    ["SandboxApps:Apps:demo:Name"] = "Demo",
                    ["SandboxApps:Apps:demo:Executable"] = "/plugins/sandbox-apps/demo",
                }
            )
            .Build();

        var catalog = SandboxAppCatalog.Load(configuration);
        catalog.IsAvailableFor("chat.example.test", true, true).Should().BeTrue();
        catalog.IsAvailableFor("chat.example.test", false, true).Should().BeFalse();
        catalog.IsAvailableFor("evil.test", true, true).Should().BeFalse();
        catalog.IsAvailableFor("chat.example.test", true, false).Should().BeFalse();
        catalog.TryGet("demo", out _).Should().BeTrue();
        catalog.TryGet("other", out _).Should().BeFalse();
    }

    [Fact]
    public void Catalog_AllowsACapableWorkspaceBeforeItsFirstMiniWebAppExists()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["SandboxApps:Enabled"] = "true",
                    ["SandboxApps:SiteDomain"] = "example.test",
                    ["SandboxApps:AppDomain"] = "apps.example.test",
                }
            )
            .Build();

        var catalog = SandboxAppCatalog.Load(configuration);
        catalog.IsAvailableFor("chat.example.test", true, true).Should().BeTrue();
        catalog.Apps.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/workspace/demo.py")]
    [InlineData("/opt/sandbox-apps/demo.py")]
    [InlineData("/plugins/sandbox-apps/../other/demo.py")]
    public void Catalog_RejectsExecutableOutsideTrustedGlobalPluginMount(string executable)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["SandboxApps:Enabled"] = "true",
                    ["SandboxApps:SiteDomain"] = "example.test",
                    ["SandboxApps:AppDomain"] = "apps.example.test",
                    ["SandboxApps:Apps:demo:Executable"] = executable,
                }
            )
            .Build();

        Action load = () => SandboxAppCatalog.Load(configuration);
        load.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void LaunchTicket_IsOneUseAndBoundToHost()
    {
        var store = new SandboxAppInstanceStore(TimeProvider.System);
        var launch = store.Issue(
            "thread",
            "default",
            DemoApp,
            User,
            "apps.example.test",
            DateTimeOffset.UtcNow.AddMinutes(5)
        );

        store.Exchange(launch.Ticket, "wrong.apps.example.test").Should().BeNull();
        var grant = store.Exchange(launch.Ticket, launch.Host);
        grant.Should().NotBeNull();
        store.Exchange(launch.Ticket, launch.Host).Should().BeNull();
        store.Authenticate(grant!.Cookie, launch.Host)!.ThreadId.Should().Be("thread");
        grant.WorkspaceId.Should().Be("default");
        store.Authenticate(grant.Cookie, "wrong.apps.example.test").Should().BeNull();
    }

    [Fact]
    public void LaunchTicket_PinsTheExecutableVersionForTheGrant()
    {
        var store = new SandboxAppInstanceStore(TimeProvider.System);
        var original = new SandboxAppDefinition(
            "demo",
            "Demo",
            "/plugins/sandbox-apps/demo/v1.py",
            [],
            null,
            65536,
            8388608,
            TimeSpan.FromSeconds(30)
        );
        var launch = store.Issue(
            "thread",
            "default",
            original,
            User,
            "apps.example.test",
            DateTimeOffset.UtcNow.AddMinutes(5)
        );

        var grant = store.Exchange(launch.Ticket, launch.Host);
        grant.Should().NotBeNull();
        grant!.App.Executable.Should().Be("/plugins/sandbox-apps/demo/v1.py");
    }

    [Fact]
    public void LaunchTicketAndGrant_ExpireIndependently()
    {
        var clock = new FakeTimeProvider();
        var store = new SandboxAppInstanceStore(clock);
        var stale = store.Issue(
            "thread",
            "default",
            DemoApp,
            User,
            "apps.example.test",
            clock.GetUtcNow().AddMinutes(5)
        );
        clock.Advance(TimeSpan.FromSeconds(61));
        store.Exchange(stale.Ticket, stale.Host).Should().BeNull();

        var fresh = store.Issue(
            "thread",
            "default",
            DemoApp,
            User,
            "apps.example.test",
            clock.GetUtcNow().AddMinutes(5)
        );
        var grant = store.Exchange(fresh.Ticket, fresh.Host)!;
        clock.Advance(TimeSpan.FromMinutes(6));
        store.Authenticate(grant.Cookie, fresh.Host).Should().BeNull();
    }

    [Fact]
    public async Task CgiParser_StreamsOnlyAfterValidatedHeaders()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var parser = new SandboxCgiResponse(context.Response);

        await parser.WriteAsync("Status: 201 Created\r\nContent-Type: text/plain\r\n\r\nfirst"u8.ToArray(), default);
        context.Response.StatusCode.Should().Be(201);
        context.Response.ContentType.Should().Be("text/plain");
        await parser.WriteAsync(" second"u8.ToArray(), default);
        await parser.CompleteAsync(default);
        ((MemoryStream)context.Response.Body).ToArray().Should().Equal("first second"u8.ToArray());
    }

    [Theory]
    [InlineData("Set-Cookie: x=y\r\nContent-Type: text/html\r\n\r\nx")]
    [InlineData("Location: https://evil.test/\r\nContent-Type: text/html\r\n\r\nx")]
    [InlineData("Content-Type: text/html\r\nX-Frame-Options: DENY\r\n\r\nx")]
    public async Task CgiParser_RejectsForbiddenHeaders(string output)
    {
        var context = new DefaultHttpContext();
        var parser = new SandboxCgiResponse(context.Response);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            parser.WriteAsync(System.Text.Encoding.UTF8.GetBytes(output), default)
        );
    }

    [Fact]
    public async Task CgiParser_RejectsOversizedAndIncompleteHeaders()
    {
        var oversized = new SandboxCgiResponse(new DefaultHttpContext().Response);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            oversized.WriteAsync(System.Text.Encoding.ASCII.GetBytes("Content-Type: " + new string('x', 8192)), default)
        );
        var incomplete = new SandboxCgiResponse(new DefaultHttpContext().Response);
        await incomplete.WriteAsync("Content-Type: text/html\r\n"u8.ToArray(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => incomplete.CompleteAsync(default));
    }

    [Fact]
    public async Task Access_RefusesAnonymousViewerAndUnknownThreadBeforeSandboxResolution()
    {
        var metadata = new ThreadMetadata
        {
            ThreadId = "thread",
            LastUpdated = 0,
            TenantId = "tenant",
            OwnerUserId = "owner",
            Visibility = Visibility.Private,
            Properties = ImmutableDictionary<string, object>.Empty.Add(
                MultiTurnAgentPool.WorkspacePropertyKey,
                "default"
            ),
        };
        var conversations = new Mock<IConversationStore>();
        conversations.Setup(x => x.LoadMetadataAsync("thread", It.IsAny<CancellationToken>())).ReturnsAsync(metadata);
        conversations
            .Setup(x => x.LoadMetadataAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ThreadMetadata?)null);
        var grants = new InMemoryResourceGrantStore();
        await grants.GrantAsync(
            new ResourceGrant
            {
                TenantId = "tenant",
                Resource = new ResourceRef(ResourceTypes.Conversation, "thread"),
                SubjectId = "user",
                Role = GrantRole.Viewer,
                GrantedBy = "owner",
                GrantedAt = DateTimeOffset.UtcNow,
            }
        );
        var browser = new FakeFileBrowser();
        var access = new SandboxAppAccess(conversations.Object, browser, TestAuthorizers.Enforcing(User, grants));

        (await access.ResolveAsync("thread", null, default)).Status.Should().Be(401);
        (await access.ResolveAsync("thread", User, default)).Status.Should().Be(403);
        (await access.ResolveAsync("missing", User, default)).Status.Should().Be(404);
        browser.ResolveCredentials.Should().BeEmpty();
    }

    [Fact]
    public async Task Launch_RequiresThePersistedWorkspaceId()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["SandboxApps:Enabled"] = "true",
                    ["SandboxApps:SiteDomain"] = "example.test",
                    ["SandboxApps:AppDomain"] = "apps.example.test",
                    ["SandboxApps:HttpsPort"] = "5011",
                    ["SandboxApps:Apps:demo:Executable"] = "/plugins/sandbox-apps/demo",
                }
            )
            .Build();
        var catalog = SandboxAppCatalog.Load(configuration);
        var conversations = new Mock<IConversationStore>();
        conversations
            .Setup(x => x.LoadMetadataAsync("thread", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new ThreadMetadata
                {
                    ThreadId = "thread",
                    LastUpdated = 0,
                    TenantId = "tenant",
                    OwnerUserId = "user",
                    Visibility = Visibility.Private,
                    Properties = ImmutableDictionary<string, object>.Empty.Add(
                        MultiTurnAgentPool.WorkspacePropertyKey,
                        "default"
                    ),
                }
            );
        var browser = new Mock<IWorkspaceFileBrowser>();
        browser
            .Setup(x => x.ResolveThreadWorkspaceSessionAsync("thread", "default", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new SandboxSessionResolution(
                    SandboxSessionResolutionOutcome.Resolved,
                    new SandboxSession("default", "session", "/workspace", "/host/workspace"),
                    null,
                    null
                )
            );
        browser.Setup(x => x.SupportsStreamingAsync("session", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var authorizer = TestAuthorizers.Enforcing(User);
        var controller = new SandboxAppsController(
            catalog,
            new SandboxAppDiscovery(browser.Object),
            new SandboxAppCapability(browser.Object),
            new SandboxAppAccess(conversations.Object, browser.Object, authorizer),
            new SandboxAppInstanceStore(TimeProvider.System),
            authorizer
        )
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        controller.HttpContext.Request.Scheme = "https";
        controller.HttpContext.Request.Host = new HostString("chat.example.test");

        (await controller.Launch("thread", "demo", null, default))
            .Should()
            .BeOfType<Microsoft.AspNetCore.Mvc.NotFoundResult>();
        (await controller.Launch("thread", "demo", "other", default))
            .Should()
            .BeOfType<Microsoft.AspNetCore.Mvc.NotFoundResult>();
        browser.Verify(x => x.SupportsStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var accepted = await controller.Launch("thread", "demo", "default", default);
        var payload = System.Text.Json.JsonSerializer.Serialize(
            accepted.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>().Subject.Value
        );
        payload.Should().Contain("https://").And.Contain(":5011/_launch");
    }

    [Fact]
    public async Task AppHost_StreamsApprovedCommandAndNeverFallsThroughToChatRoutes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["SandboxApps:Enabled"] = "true",
                    ["SandboxApps:SiteDomain"] = "example.test",
                    ["SandboxApps:AppDomain"] = "apps.example.test",
                    ["SandboxApps:HttpsPort"] = "5011",
                    ["SandboxApps:Apps:demo:Executable"] = "/plugins/sandbox-apps/demo",
                }
            )
            .Build();
        var catalog = SandboxAppCatalog.Load(configuration);
        var owner = User;
        var authorizer = TestAuthorizers.Enforcing(owner);
        var metadata = new ThreadMetadata
        {
            ThreadId = "thread",
            LastUpdated = 0,
            TenantId = "tenant",
            OwnerUserId = "user",
            Visibility = Visibility.Private,
            Properties = ImmutableDictionary<string, object>.Empty.Add(
                MultiTurnAgentPool.WorkspacePropertyKey,
                "default"
            ),
        };
        var conversations = new Mock<IConversationStore>();
        conversations.Setup(x => x.LoadMetadataAsync("thread", It.IsAny<CancellationToken>())).ReturnsAsync(metadata);
        var browser = new Mock<IWorkspaceFileBrowser>();
        browser
            .Setup(x => x.ResolveThreadWorkspaceSessionAsync("thread", "default", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new SandboxSessionResolution(
                    SandboxSessionResolutionOutcome.Resolved,
                    new SandboxSession("default", "session", "/workspace", "/host/workspace"),
                    null,
                    null
                )
            );
        browser
            .Setup(x =>
                x.ExecuteWorkspaceCommandStreamingAsync(
                    "session",
                    It.IsAny<SandboxCommand>(),
                    It.IsAny<Func<SandboxOutputChunk, CancellationToken, ValueTask>>(),
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<IReadOnlyDictionary<string, string>>(),
                    It.IsAny<long>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                async (
                    string _,
                    SandboxCommand command,
                    Func<SandboxOutputChunk, CancellationToken, ValueTask> callback,
                    ReadOnlyMemory<byte> stdin,
                    IReadOnlyDictionary<string, string>? env,
                    long _,
                    CancellationToken ct
                ) =>
                {
                    command.Arguments[0].Should().Be("/plugins/sandbox-apps/demo");
                    if (env!["PATH_INFO"] == "/")
                    {
                        await callback(
                            new SandboxOutputChunk(
                                SandboxOutputStream.Stdout,
                                "Set-Cookie: forbidden=1\r\n\r\n"u8.ToArray()
                            ),
                            ct
                        );
                        return new SandboxStreamResult(0, 28, 0);
                    }
                    env["PATH_INFO"].Should().BeOneOf("/api/data", "/data");
                    if (env["REQUEST_METHOD"] == "POST")
                    {
                        if (env["CONTENT_TYPE"] == "application/json")
                        {
                            System.Text.Encoding.ASCII.GetString(stdin.Span).Should().Contain("\"selection\":\"ok\"");
                            env["HTTP_X_CSRF_TOKEN"].Should().Be(env["SANDBOX_APP_CSRF_TOKEN"]);
                        }
                        else
                        {
                            System.Text.Encoding.ASCII.GetString(stdin.Span).Should().Contain("selection=ok");
                        }
                    }
                    await callback(
                        new SandboxOutputChunk(
                            SandboxOutputStream.Stdout,
                            "Content-Type: application/json\r\n\r\n{\"ok\":true}"u8.ToArray()
                        ),
                        ct
                    );
                    return new SandboxStreamResult(0, 53, 0);
                }
            );
        var instances = new SandboxAppInstanceStore(TimeProvider.System);
        var launch = instances.Issue(
            "thread",
            "default",
            DemoApp,
            owner,
            catalog.AppDomain,
            DateTimeOffset.UtcNow.AddMinutes(5)
        );
        var grant = instances.Exchange(launch.Ticket, launch.Host)!;
        var fallthrough = 0;
        var middleware = new SandboxAppMiddleware(
            _ =>
            {
                fallthrough++;
                return Task.CompletedTask;
            },
            catalog,
            instances,
            new SandboxAppAccess(conversations.Object, browser.Object, authorizer),
            browser.Object,
            authorizer,
            NullLogger<SandboxAppMiddleware>.Instance
        );

        var wrongWorkspaceLaunch = instances.Issue(
            "thread",
            "other-workspace",
            DemoApp,
            owner,
            catalog.AppDomain,
            DateTimeOffset.UtcNow.AddMinutes(5)
        );
        var wrongWorkspaceGrant = instances.Exchange(wrongWorkspaceLaunch.Ticket, wrongWorkspaceLaunch.Host)!;
        var wrongWorkspaceRequest = new DefaultHttpContext();
        wrongWorkspaceRequest.Request.Scheme = "https";
        wrongWorkspaceRequest.Request.Method = "GET";
        wrongWorkspaceRequest.Request.Host = new HostString(wrongWorkspaceLaunch.Host);
        wrongWorkspaceRequest.Request.Path = "/data";
        wrongWorkspaceRequest.Request.Headers.Cookie = $"__Host-sandbox-app={wrongWorkspaceGrant.Cookie}";
        wrongWorkspaceRequest.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        wrongWorkspaceRequest.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(wrongWorkspaceRequest);
        wrongWorkspaceRequest.Response.StatusCode.Should().Be(403);

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Method = "GET";
        context.Request.Host = new HostString(launch.Host);
        context.Request.Path = "/api/data";
        context.Request.Headers.Cookie = $"__Host-sandbox-app={grant.Cookie}";
        context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        context.Response.StatusCode.Should().Be(200);
        context
            .Response.Headers.ContentSecurityPolicy.ToString()
            .Should()
            .Contain("frame-ancestors https://*.example.test:5011");
        ((MemoryStream)context.Response.Body).ToArray().Should().Equal("{\"ok\":true}"u8.ToArray());

        var noGrantPage = new DefaultHttpContext();
        noGrantPage.Request.Scheme = "https";
        noGrantPage.Request.Method = "GET";
        noGrantPage.Request.Host = new HostString(launch.Host);
        noGrantPage.Request.Path = "/";
        noGrantPage.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        noGrantPage.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(noGrantPage);
        noGrantPage.Response.StatusCode.Should().Be(401);
        noGrantPage.Response.ContentType.Should().StartWith("text/html");
        System
            .Text.Encoding.UTF8.GetString(((MemoryStream)noGrantPage.Response.Body).ToArray())
            .Should()
            .Contain("sandbox-app-failed");

        var noGrantAsset = new DefaultHttpContext();
        noGrantAsset.Request.Scheme = "https";
        noGrantAsset.Request.Method = "GET";
        noGrantAsset.Request.Host = new HostString(launch.Host);
        noGrantAsset.Request.Path = "/assets/app.js";
        noGrantAsset.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        noGrantAsset.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(noGrantAsset);
        noGrantAsset.Response.StatusCode.Should().Be(401);
        ((MemoryStream)noGrantAsset.Response.Body).Length.Should().Be(0);

        var api = new DefaultHttpContext();
        api.Request.Scheme = "https";
        api.Request.Method = "GET";
        api.Request.Host = new HostString(launch.Host);
        api.Request.Path = "/api/identity/config";
        await middleware.InvokeAsync(api);
        api.Response.StatusCode.Should().Be(401);
        var crossSite = new DefaultHttpContext();
        crossSite.Request.Scheme = "https";
        crossSite.Request.Method = "GET";
        crossSite.Request.Host = new HostString(launch.Host);
        crossSite.Request.Path = "/data";
        crossSite.Request.Headers.Cookie = $"__Host-sandbox-app={grant.Cookie}";
        crossSite.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        await middleware.InvokeAsync(crossSite);
        crossSite.Response.StatusCode.Should().Be(403);

        var noCsrf = new DefaultHttpContext();
        noCsrf.Request.Scheme = "https";
        noCsrf.Request.Method = "POST";
        noCsrf.Request.Host = new HostString(launch.Host);
        noCsrf.Request.Path = "/data";
        noCsrf.Request.Headers.Cookie = $"__Host-sandbox-app={grant.Cookie}";
        noCsrf.Request.Body = new MemoryStream("{}"u8.ToArray());
        await middleware.InvokeAsync(noCsrf);
        noCsrf.Response.StatusCode.Should().Be(403);

        var validPost = new DefaultHttpContext();
        validPost.Request.Scheme = "https";
        validPost.Request.Method = "POST";
        validPost.Request.Host = new HostString(launch.Host);
        validPost.Request.Path = "/data";
        validPost.Request.ContentType = "application/x-www-form-urlencoded";
        validPost.Request.Headers.Cookie = $"__Host-sandbox-app={grant.Cookie}";
        validPost.Request.Body = new MemoryStream(
            System.Text.Encoding.ASCII.GetBytes($"_csrf={grant.CsrfToken}&selection=ok")
        );
        validPost.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(validPost);
        validPost.Response.StatusCode.Should().Be(200);
        ((MemoryStream)validPost.Response.Body).ToArray().Should().Equal("{\"ok\":true}"u8.ToArray());

        var validJsonPost = new DefaultHttpContext();
        validJsonPost.Request.Scheme = "https";
        validJsonPost.Request.Method = "POST";
        validJsonPost.Request.Host = new HostString(launch.Host);
        validJsonPost.Request.Path = "/api/data";
        validJsonPost.Request.ContentType = "application/json";
        validJsonPost.Request.Headers.Cookie = $"__Host-sandbox-app={grant.Cookie}";
        validJsonPost.Request.Headers["X-CSRF-Token"] = grant.CsrfToken;
        validJsonPost.Request.Body = new MemoryStream("{\"selection\":\"ok\"}"u8.ToArray());
        validJsonPost.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(validJsonPost);
        validJsonPost.Response.StatusCode.Should().Be(200);
        ((MemoryStream)validJsonPost.Response.Body).ToArray().Should().Equal("{\"ok\":true}"u8.ToArray());

        var wrongCsrf = new DefaultHttpContext();
        wrongCsrf.Request.Scheme = "https";
        wrongCsrf.Request.Method = "POST";
        wrongCsrf.Request.Host = new HostString(launch.Host);
        wrongCsrf.Request.Path = "/data";
        wrongCsrf.Request.ContentType = "application/x-www-form-urlencoded";
        wrongCsrf.Request.Headers.Cookie = $"__Host-sandbox-app={grant.Cookie}";
        wrongCsrf.Request.Body = new MemoryStream("_csrf=wrong&selection=ok"u8.ToArray());
        await middleware.InvokeAsync(wrongCsrf);
        wrongCsrf.Response.StatusCode.Should().Be(403);

        var failedPage = new DefaultHttpContext();
        failedPage.Request.Scheme = "https";
        failedPage.Request.Method = "GET";
        failedPage.Request.Host = new HostString(launch.Host);
        failedPage.Request.Path = "/";
        failedPage.Request.Headers.Cookie = $"__Host-sandbox-app={grant.Cookie}";
        failedPage.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        failedPage.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(failedPage);
        failedPage.Response.StatusCode.Should().Be(502);
        failedPage.Response.ContentType.Should().StartWith("text/html");
        var failedHtml = System.Text.Encoding.UTF8.GetString(((MemoryStream)failedPage.Response.Body).ToArray());
        failedHtml.Should().Contain("sandbox-app-failed");
        failedHtml.Should().NotContain(grant.Cookie).And.NotContain(grant.CsrfToken);

        var oversizedBody = new DefaultHttpContext();
        oversizedBody.Request.Scheme = "https";
        oversizedBody.Request.Method = "POST";
        oversizedBody.Request.Host = new HostString(launch.Host);
        oversizedBody.Request.Path = "/data";
        oversizedBody.Request.Headers.Cookie = $"__Host-sandbox-app={grant.Cookie}";
        oversizedBody.Request.ContentLength = 65537;
        await middleware.InvokeAsync(oversizedBody);
        oversizedBody.Response.StatusCode.Should().Be(413);

        var nextLaunch = instances.Issue(
            "thread",
            "default",
            DemoApp,
            owner,
            catalog.AppDomain,
            DateTimeOffset.UtcNow.AddMinutes(5)
        );
        var exchange = new DefaultHttpContext();
        exchange.Request.Scheme = "https";
        exchange.Request.Method = "POST";
        exchange.Request.Host = new HostString(nextLaunch.Host);
        exchange.Request.Path = "/_launch";
        exchange.Request.ContentType = "application/x-www-form-urlencoded";
        exchange.Request.Body = new MemoryStream(System.Text.Encoding.ASCII.GetBytes($"ticket={nextLaunch.Ticket}"));
        await middleware.InvokeAsync(exchange);
        exchange.Response.StatusCode.Should().Be(303);
        exchange.Response.Headers.Location.ToString().Should().Be("/");
        var cookie = exchange.Response.Headers.SetCookie.ToString();
        cookie.ToLowerInvariant().Should().Contain("httponly").And.Contain("secure").And.Contain("samesite=strict");
        cookie.ToLowerInvariant().Should().NotContain("domain=");
        await middleware.InvokeAsync(exchange);
        exchange.Response.StatusCode.Should().Be(403);

        var badExchange = new DefaultHttpContext();
        badExchange.Request.Scheme = "https";
        badExchange.Request.Method = "POST";
        badExchange.Request.Host = new HostString(nextLaunch.Host);
        badExchange.Request.Path = "/_launch";
        badExchange.Request.ContentType = "application/x-www-form-urlencoded";
        badExchange.Request.Body = new MemoryStream("ticket=invalid"u8.ToArray());
        badExchange.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(badExchange);
        badExchange.Response.StatusCode.Should().Be(403);
        var exchangeHtml = System.Text.Encoding.UTF8.GetString(((MemoryStream)badExchange.Response.Body).ToArray());
        exchangeHtml.Should().Contain("sandbox-app-failed");
        exchangeHtml.Should().NotContain(grant.Cookie).And.NotContain(grant.CsrfToken);

        foreach (
            var (method, contentType, scheme) in new[]
            {
                ("GET", null, "https"),
                ("POST", "application/json", "https"),
                ("POST", "application/x-www-form-urlencoded", "http"),
            }
        )
        {
            var refused = new DefaultHttpContext();
            refused.Request.Scheme = scheme;
            refused.Request.Method = method;
            refused.Request.Host = new HostString(nextLaunch.Host);
            refused.Request.Path = "/_launch";
            refused.Request.ContentType = contentType;
            refused.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(refused);
            refused.Response.StatusCode.Should().Be(404);
            System
                .Text.Encoding.UTF8.GetString(((MemoryStream)refused.Response.Body).ToArray())
                .Should()
                .Contain("sandbox-app-failed");
        }

        var disabledConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["SandboxApps:Enabled"] = "false",
                    ["SandboxApps:SiteDomain"] = "example.test",
                    ["SandboxApps:AppDomain"] = "apps.example.test",
                }
            )
            .Build();
        var disabled = new SandboxAppMiddleware(
            _ =>
            {
                fallthrough++;
                return Task.CompletedTask;
            },
            SandboxAppCatalog.Load(disabledConfig),
            instances,
            new SandboxAppAccess(conversations.Object, browser.Object, authorizer),
            browser.Object,
            authorizer,
            NullLogger<SandboxAppMiddleware>.Instance
        );
        var disabledLaunch = new DefaultHttpContext();
        disabledLaunch.Request.Scheme = "https";
        disabledLaunch.Request.Method = "POST";
        disabledLaunch.Request.Host = new HostString(nextLaunch.Host);
        disabledLaunch.Request.Path = "/_launch";
        disabledLaunch.Response.Body = new MemoryStream();
        await disabled.InvokeAsync(disabledLaunch);
        disabledLaunch.Response.StatusCode.Should().Be(404);
        System
            .Text.Encoding.UTF8.GetString(((MemoryStream)disabledLaunch.Response.Body).ToArray())
            .Should()
            .Contain("sandbox-app-failed");

        var disabledRoot = new DefaultHttpContext();
        disabledRoot.Request.Scheme = "https";
        disabledRoot.Request.Method = "GET";
        disabledRoot.Request.Host = new HostString(nextLaunch.Host);
        disabledRoot.Request.Path = "/";
        disabledRoot.Response.Body = new MemoryStream();
        await disabled.InvokeAsync(disabledRoot);
        disabledRoot.Response.StatusCode.Should().Be(404);
        ((MemoryStream)disabledRoot.Response.Body).Length.Should().Be(0);

        fallthrough.Should().Be(0);
        browser.Verify(
            x =>
                x.ExecuteWorkspaceCommandStreamingAsync(
                    It.IsAny<string>(),
                    It.IsAny<SandboxCommand>(),
                    It.IsAny<Func<SandboxOutputChunk, CancellationToken, ValueTask>>(),
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<IReadOnlyDictionary<string, string>>(),
                    It.IsAny<long>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Exactly(4)
        );
    }
}
