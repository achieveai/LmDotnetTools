using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Pins the identity boundary against the REAL host's pipeline and route table (#345, #346).
/// </summary>
/// <remarks>
/// <para>
/// Both defects were ordering defects, and ordering is a property of <c>Program.cs</c>, not of any
/// one component. A synthetic pipeline that mirrors the intended order proves the components work
/// when composed correctly; it cannot prove <c>Program.cs</c> composes them that way. Only the real
/// host can, so this suite boots it.
/// </para>
/// <para>
/// The route-partition test is here for a sharper reason. This repository has already shipped one
/// "there is a single seam" claim that a sibling controller falsified, and the identity boundary is
/// exactly that shape: a prefix guard plus a hand-maintained list of exemptions. Enumerating the
/// host's actual <see cref="EndpointDataSource"/> - which includes controllers the SDK's generated
/// <c>ApplicationPartAttribute</c> pulls in from referenced assemblies, not just the ones written in
/// this sample - is the only way to notice a route that lands outside the boundary without anyone
/// deciding it should.
/// </para>
/// </remarks>
public sealed class IdentityBoundaryPipelineTests : LoggingTestBase
{
    private const string Secret = "e2e-inbound-secret-value";
    private const string DaemonAppId = "review-daemon";
    private const string DaemonTenant = "tnt_daemon";
    private const string ClientOrigin = "https://client.example";
    private const string GuardedRoute = "/api/conversations";
    private const string CallbackHost = "callbacks.example.com";
    private const string HumanSubject = "dir-a:alice";

    public IdentityBoundaryPipelineTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public void EveryApiRouteThisHostPublishes_IsEitherGuardedOrDeliberatelyExempt()
    {
        LogTestStart();
        using var factory = NewFactory();

        var apiRoutes = ApiRoutes(factory);
        LogData("apiRoutes", apiRoutes);

        // Non-vacuity first. An empty enumeration would satisfy the partition below trivially, and
        // this test's whole value is that it fails when a NEW route appears.
        _ = apiRoutes.Should().NotBeEmpty();
        _ = apiRoutes.Should().Contain("api/conversations");

        var guarded = apiRoutes.Where(IsGuarded).ToArray();
        var exempt = apiRoutes.Where(route => !IsGuarded(route)).ToArray();

        // Both halves must be non-empty, or a guard that admitted everything - or refused
        // everything - would read as a pass.
        _ = guarded.Should().NotBeEmpty();
        _ = exempt.Should().NotBeEmpty();

        // The exempt half is the security-relevant one, so it is pinned by name. A route that joins
        // it does so by an author editing this list, never by a prefix quietly matching.
        _ = exempt
            .Should()
            .BeEquivalentTo(
                [
                    "api/identity/config",
                    "api/admin/tenants",
                    "api/admin/tenants/{tenantId}/adopt-legacy",
                    "api/auth/webhook/{provider}",
                ],
                "every /api route outside the identity boundary is a deliberate, reviewed decision - "
                    + "see IdentityMiddleware.InfrastructureApiPaths for why each one is there"
            );

        // The forward direction, which the partition above cannot see. This test enumerates routes
        // and asks whether each is guarded or exempt; a DEAD exemption contributes no route, so it is
        // invisible here by construction. That is exactly how "/api/health" survived until #350 -
        // an entry naming a path this host maps nowhere, granting nothing observable, while silently
        // reserving the whole subtree beneath it for the day a route lands there.
        //
        // IsGuardedApiPath matches with StartsWithSegments, so the requirement on an entry is that it
        // PREFIXES a published route, not that it equals one: "api/admin/tenants" legitimately covers
        // "api/admin/tenants/{tenantId}/adopt-legacy", and "api/auth/webhook" covers
        // "api/auth/webhook/{provider}".
        foreach (var unguarded in IdentityMiddleware.UnguardedApiPaths)
        {
            var prefix = unguarded.TrimStart('/');

            _ = apiRoutes
                .Should()
                .Contain(
                    route => route == prefix || route.StartsWith(prefix + "/", StringComparison.Ordinal),
                    "every entry in IdentityMiddleware.UnguardedApiPaths must name a route this host "
                        + $"actually maps, and '{unguarded}' names none - a dead exemption grants nothing "
                        + "today and silently reserves its whole subtree for tomorrow (#350)"
                );
        }

        // Both egress-key routes are INSIDE the boundary, not exempt (BE1). The controller presents
        // no credential of its own - it is loopback-gated only - so leaving it outside enforcement
        // let a credential-less loopback caller manage egress keys under Identity:Enforce. Pinned
        // by name here as the counterpart to the exempt list: an edit that carves either back out
        // has to defeat this assertion too, not just quietly extend a prefix.
        _ = guarded.Should().Contain("api/auth/egress-keys");
        _ = guarded.Should().Contain("api/auth/egress-keys/{id}");
    }

    [Fact]
    public void EveryRouteFamilyThisHostPublishes_IsAccountedFor_NotJustTheApiOnes()
    {
        LogTestStart();
        using var factory = NewFactory();

        // The partition test above answers "is every /api route guarded?" and CANNOT answer "is
        // every route guarded?". #342 is precisely the second question: /ws sits outside the /api
        // prefix, so a boundary asserted over that prefix alone said nothing about it and the
        // transport stayed open while enforcement was on. Nothing enumerated the routes that are
        // NOT under /api, which is the only place a defect of that shape can hide.
        var nonApiRoutes = AllRoutes(factory)
            .Where(route => !route.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        LogData("nonApiRoutes", nonApiRoutes);

        _ = nonApiRoutes
            .Should()
            .BeEquivalentTo(
                ["auth/m365/callback", "auth/{providerId}", "ws", "ws/subagent", "{*path:nonfile}"],
                "a route family outside /api joins this list by an author editing it, never by nobody "
                    + "looking - #342 is what the absence of this enumeration cost"
            );

        // The transports are inside the boundary now, asked of the real predicate.
        _ = IdentityMiddleware.IsGuardedPath(new PathString("/ws")).Should().BeTrue();
        _ = IdentityMiddleware.IsGuardedPath(new PathString("/ws/subagent")).Should().BeTrue();

        // The rest stay outside, each for a reason that survives being written down:
        //  - the SPA fallback serves the very screen that explains a refusal, so gating it would
        //    hide the explanation behind the thing it explains;
        //  - the two OAuth pages are reached by a redirect FROM an identity provider, which carries
        //    no bearer token by construction, so guarding them refuses every legitimate arrival.
        //    They render sign-in state for a provider, never conversation content.
        _ = IdentityMiddleware.IsGuardedPath(new PathString("/")).Should().BeFalse();
        _ = IdentityMiddleware.IsGuardedPath(new PathString("/dist/index.html")).Should().BeFalse();
        _ = IdentityMiddleware.IsGuardedPath(new PathString("/auth/github")).Should().BeFalse();
        _ = IdentityMiddleware.IsGuardedPath(new PathString("/auth/m365/callback")).Should().BeFalse();
    }

    [Fact]
    public void WithTheLifecycleControlPlaneOn_ItsRoutesAreInsideTheIdentityBoundary()
    {
        LogTestStart();

        // The partition test above boots a DEFAULT host, on which the lifecycle routes do not exist
        // (they are config-gated and absent unless both flags are set). So whatever
        // IdentityMiddleware does with /api/lifecycle is invisible to it - the one route family the
        // enumeration cannot see is the one the enumeration is meant to catch. This boots the plane
        // ON and asks the real predicate about the real, published routes.
        //
        // This test used to assert the OPPOSITE, pinning /api/lifecycle as an exempt-by-decision
        // carve-out. #402 retired that decision. The carve-out's stated basis was that the plane is
        // "gated behind its own signature check"; it is not. LifecycleApprovalController's own
        // remarks say it "does not authenticate" and that "no subscriber-to-host signing convention
        // exists", and the plane's only signing is OUTBOUND in HttpLifecycleDeliverySender. So the
        // exemption granted no authority and cost tenant refusal: under Identity:Enforce a suspended
        // or not-provisioned tenant's still-valid token was never answered, reached these
        // controllers, and satisfied their AuthenticatedAppId() - which reads the raw ClaimsPrincipal.
        var settings = EnforcingSettings();
        settings["Lifecycle:Delivery:Enabled"] = "true";
        settings["Lifecycle:Delivery:AllowedCallbackHosts:0"] = "callbacks.example.com";
        settings["Lifecycle:Approval:Enabled"] = "true";

        using var factory = NewFactory(settings);

        var apiRoutes = ApiRoutes(factory);
        LogData("apiRoutes", apiRoutes);

        // Non-vacuity: the flags actually published the plane. Without this, a plane that failed to
        // register would make the assertions below trivially true - IsGuarded would be answering
        // about routes nobody serves.
        _ = apiRoutes.Should().Contain("api/lifecycle/subscriptions");
        _ = apiRoutes.Should().Contain("api/lifecycle/approvals/decisions");

        // The reviewed decision, asked of the real predicate: lifecycle is a service-to-service
        // surface with a front door that can speak for it (ServiceCallerPrincipalSource), so it is
        // guarded like every other one.
        _ = IsGuarded("api/lifecycle/subscriptions")
            .Should()
            .BeTrue(
                "the lifecycle control plane sits inside the identity boundary and honours tenant " + "refusal (#402)"
            );
        _ = IsGuarded("api/lifecycle/approvals/decisions")
            .Should()
            .BeTrue(
                "the lifecycle control plane sits inside the identity boundary and honours tenant " + "refusal (#402)"
            );
    }

    [Fact]
    public async Task WithEnforcementOn_ACorsPreflight_SurvivesTheRealPipeline()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings());
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Options, new Uri(GuardedRoute, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Origin", ClientOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        // A browser never attaches Authorization to a preflight, by specification. If identity runs
        // first the preflight is 401'd, no Access-Control-Allow-Origin is written, and the browser
        // abandons the real request before sending it.
        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        _ = response
            .Headers.GetValues("Access-Control-Allow-Origin")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(ClientOrigin);
    }

    [Fact]
    public async Task WithEnforcementOn_ARefusalFromTheRealPipeline_IsStillReadableCrossOrigin()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings());
        using var client = factory.CreateClient();

        // A service caller whose secret matches but whose app id is not onboarded: authenticated,
        // and refused by identity with a stable code. That is the refusal shape the SPA's rejection
        // screen is built to read.
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(GuardedRoute, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-S2S-Auth", Secret);
        request.Headers.TryAddWithoutValidation("X-Sbx-App-Id", "not-onboarded");
        request.Headers.TryAddWithoutValidation("Origin", ClientOrigin);

        var response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _ = response
            .Headers.GetValues(IdentityMiddleware.RefusalCodeHeader)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(ServiceCallerPrincipalSource.AppNotRegisteredCode);
        _ = response
            .Headers.GetValues("Access-Control-Allow-Origin")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                ClientOrigin,
                "a refusal written downstream of the CORS middleware leaves without this header, "
                    + "and a cross-origin client then sees an opaque network error instead of the "
                    + "explanation the refusal exists to give it"
            );
    }

    [Fact]
    public async Task WithEnforcementOn_TheDaemonsHeaderShape_ReachesTheEndpointOnTheRealPipeline()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings());
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(GuardedRoute, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-S2S-Auth", Secret);
        request.Headers.TryAddWithoutValidation("X-Sbx-App-Id", DaemonAppId);
        request.Headers.TryAddWithoutValidation("X-Sbx-App-Key", "app-key-value");

        var response = await client.SendAsync(request);

        // The whole point of #345: this exact request used to be 401'd by the identity middleware
        // before the endpoint's own S2S guard ever ran.
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WithEnforcementOn_AnAnonymousBrowserRequest_IsStillRefused()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings());
        using var client = factory.CreateClient();

        // The non-vacuity partner of the test above. Without this, a build that simply stopped
        // guarding /api would pass every other assertion here.
        var response = await client.GetAsync(new Uri(GuardedRoute, UriKind.Relative));

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = response
            .Headers.GetValues(IdentityMiddleware.RefusalCodeHeader)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("authentication_required");
    }

    [Fact]
    public async Task WithEnforcementOn_ARegisteredServiceCaller_ReachesTheLifecycleControlPlane()
    {
        LogTestStart();

        // #424. Passing the identity boundary was never the whole journey. The two lifecycle
        // controllers live in LmAgentInfra, cannot see this sample's Principal type, and read
        // HttpContext.User; IdentityMiddleware published its minted principal only on
        // HttpContext.Items. Nothing bridged the two, and the only registered authentication scheme
        // is JWT bearer - which the daemon's S2S headers do not trigger. So a caller that identity
        // had just accepted arrived at Register() with User.Identity.IsAuthenticated false and was
        // refused 403 "caller is not authenticated", by the host's own control plane, for being
        // exactly the kind of caller that plane exists to serve.
        using var factory = NewFactory(LifecycleEnforcingSettings());
        using var client = factory.CreateClient();

        var response = await client.SendAsync(RegistrationRequest(daemonHeaders: true));
        var body = await response.Content.ReadAsStringAsync();
        LogData("registrationBody", body);

        _ = response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Created,
                "a caller the identity boundary admitted as {0} must be authenticated to the controllers "
                    + "behind it, not just to the middleware in front of them",
                DaemonAppId
            );

        // Non-vacuity for the status alone: a 201 proves the action ran, and the minted subscription
        // proves it ran as an owner rather than reaching some shared, ownerless path.
        _ = body.Should().Contain("subscription_id");
        _ = body.Should().Contain("signing_secret");
    }

    [Fact]
    public async Task WithEnforcementOn_ASignedInHuman_NeverReachesTheLifecycleControlPlane()
    {
        LogTestStart();

        // #433, and the reason it is an E2E test rather than a controller one. Every piece here is
        // real: a bearer scheme authenticates the caller and puts the token's `sub` on
        // ClaimTypes.NameIdentifier exactly as MapInboundClaims does, the identity boundary admits
        // the resulting interactive principal, and IdentityMiddleware's bridge deliberately does NOT
        // overwrite a principal another scheme established. So the human's own ClaimsPrincipal — not
        // any projection of an app — is what arrived at Register(), and while the action read the
        // name identifier that made any signed-in user an app: register a callback, take a signing
        // secret, and be filed as an owner under their own subject id.
        using var factory = NewFactory(LifecycleEnforcingSettings(), WithSignedInHumanBearerScheme);
        using var client = factory.CreateClient();

        var registration = RegistrationRequest(daemonHeaders: false);
        registration.Headers.TryAddWithoutValidation("Authorization", $"Bearer {HumanSubject}");
        var registrationResponse = await client.SendAsync(registration);
        var registrationBody = await registrationResponse.Content.ReadAsStringAsync();
        LogData("humanRegistrationBody", registrationBody);

        _ = registrationResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Forbidden,
                "a signed-in person is not an app, and the lifecycle plane's entire authorization model "
                    + "is that the caller names one"
            );
        _ = registrationBody
            .Should()
            .NotContain("signing_secret", "a refused caller must not be handed signing material");

        // And the refusal has to describe what actually happened. This caller authenticated - a real
        // bearer scheme validated their token and populated HttpContext.User - so answering "caller is
        // not authenticated" sends whoever reads it to inspect the one part of the pipeline that is
        // demonstrably working, while the real cause (a person is not an app) goes unnamed.
        _ = registrationBody
            .Should()
            .Contain(
                "does not name an application",
                "the refusal must name the reason the caller was refused, not a different one"
            );
        _ = registrationBody
            .Should()
            .NotContain(
                "not authenticated",
                "authentication succeeded; saying otherwise misdirects the operator reading this"
            );

        // The sibling endpoint, pinned independently. Both controllers derived the app id the same
        // way and each carries its own copy of the derivation, so one of them fixed is not the fix.
        var decision = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/lifecycle/approvals/decisions", UriKind.Relative)
        )
        {
            // Every required field present. Model validation runs before the action, so a malformed
            // body is answered 400 and never reaches the identity check this test is about — which
            // would make the assertion below pass for the wrong reason.
            Content = new StringContent(
                /*lang=json,strict*/"""
                {
                  "request_id": "req-1",
                  "subscription_id": "sub-1",
                  "decision": "allowed",
                  "arguments_hash": "0000000000000000000000000000000000000000000000000000000000000000"
                }
                """,
                Encoding.UTF8,
                "application/json"
            ),
        };
        decision.Headers.TryAddWithoutValidation("Authorization", $"Bearer {HumanSubject}");

        var decisionResponse = await client.SendAsync(decision);
        var decisionBody = await decisionResponse.Content.ReadAsStringAsync();
        LogData("humanDecisionBody", decisionBody);

        _ = decisionResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Forbidden,
                "the approval endpoint reads the caller's app identity the same way and must refuse the "
                    + "same caller"
            );
        // Same misdescription, same correction, pinned separately because the two controllers each
        // carried their own copy of the check - one of them fixed was never the fix.
        _ = decisionBody
            .Should()
            .Contain(
                "does not name an application",
                "the refusal must be the identity one, not a validation or not-found answer that would "
                    + "make this assertion pass without the fix - and it must name the reason that "
                    + "actually applied"
            );
        _ = decisionBody
            .Should()
            .NotContain(
                "not authenticated",
                "authentication succeeded; saying otherwise misdirects the operator reading this"
            );
    }

    [Fact]
    public async Task WithEnforcementOn_AnUnregisteredServiceCaller_NeverReachesTheLifecyclePlane()
    {
        LogTestStart();

        // The bridge must not be a second front door. This caller's secret matches, so it is
        // authenticated - but its app id is not onboarded, so identity refuses it at the boundary
        // and it never reaches Register(). Without this, a bridge that minted a principal for any
        // app id the caller cared to name would pass the test above unchanged.
        using var factory = NewFactory(LifecycleEnforcingSettings());
        using var client = factory.CreateClient();

        var request = RegistrationRequest(daemonHeaders: false);
        request.Headers.TryAddWithoutValidation("X-S2S-Auth", Secret);
        request.Headers.TryAddWithoutValidation("X-Sbx-App-Id", "not-onboarded");

        var response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _ = response
            .Headers.GetValues(IdentityMiddleware.RefusalCodeHeader)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(ServiceCallerPrincipalSource.AppNotRegisteredCode);
    }

    [Fact]
    public async Task WithEnforcementOff_TheDevelopmentPrincipal_StillDoesNotAuthenticateToLifecycle()
    {
        LogTestStart();

        // The narrowness pin, and the reason the bridge is keyed on the app id rather than on
        // "identity produced a principal". With Identity:Enforce off - the default every other E2E
        // test in this repository runs under - the middleware mints a development principal for an
        // anonymous request. Bridging that one would silently authenticate every anonymous caller to
        // a control plane whose whole authorization model is "the principal names an app", turning a
        // feature flag into an open subscription endpoint. The development principal names no app,
        // ToClaimsPrincipalOrNull returns null for it, and this is what that decision costs and buys.
        var settings = LifecycleSettings();
        settings["Identity:DatabasePath"] = Path.Combine(Path.GetTempPath(), $"identity_e2e_{Guid.NewGuid():N}.db");

        using var factory = NewFactory(settings);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(RegistrationRequest(daemonHeaders: false));

        _ = response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Forbidden,
                "the lifecycle control plane refuses a caller that names no app, and an unenforced host "
                    + "mints exactly such a principal for an anonymous request"
            );
        // The OTHER refusal, and the non-vacuity partner of the signed-in-human test above: with no
        // scheme wired there is no principal at all, so "not authenticated" is the accurate answer
        // here. The two cases having two messages is the whole point - one message for both told
        // every operator to go and look at their authentication scheme.
        _ = (await response.Content.ReadAsStringAsync()).Should().Contain("not authenticated");
    }

    [Fact]
    public async Task WithEnforcementOn_EgressKeyManagement_IsRefusedWithoutACredential()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings());
        using var client = factory.CreateClient();

        // BE1. The egress-key controller carries no credential of its own - it is loopback-gated
        // only, and TestServer's null remote address reads as loopback - so identity enforcement is
        // the ONLY thing standing between an anonymous caller and the key inventory. Before the fix
        // this route was in InfrastructureApiPaths, so this GET returned 200 and leaked the masked
        // inventory (and POST/DELETE could plant and destroy keys); with the carve-out removed the
        // identity middleware refuses it exactly like any other management route.
        var response = await client.GetAsync(new Uri("/api/auth/egress-keys", UriKind.Relative));

        _ = response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Unauthorized,
                "an unauthenticated caller must not reach egress-key management under Identity:Enforce"
            );
        _ = response
            .Headers.GetValues(IdentityMiddleware.RefusalCodeHeader)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("authentication_required");
    }

    [Fact]
    public async Task WithEnforcementOn_AnUnauthenticatedWebSocketHandshake_IsRefused_AndNotWith401()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings());
        using var client = factory.CreateClient();

        // The transport half of #342. /ws sits OUTSIDE the /api prefix, so before the fix flipping
        // Identity:Enforce gated the REST surface and left a fully functional unauthenticated
        // channel open beside it.
        Func<Task> handshake = () => factory.ConnectWebSocketAsync("thread-anon");

        _ = await handshake
            .Should()
            .ThrowAsync<InvalidOperationException>(
                "an unauthenticated /ws handshake must not complete under Identity:Enforce"
            );

        // And the refusal's shape, read off the same request without the handshake machinery in the
        // way. 401 is the one status a browser answers by re-authenticating, which cannot conjure a
        // WebSocket credential and therefore loops (#341's finding).
        var response = await client.GetAsync(new Uri("/ws?threadId=thread-anon", UriKind.Relative));

        _ = response
            .StatusCode.Should()
            .NotBe(
                HttpStatusCode.Unauthorized,
                "a 401 on the WebSocket transport restarts sign-in, and sign-in does not fix a missing "
                    + "handshake credential"
            );
        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _ = response
            .Headers.GetValues(IdentityMiddleware.RefusalCodeHeader)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(IdentityMiddleware.WebSocketRefusalCode);
    }

    [Fact]
    public async Task ABlankThreadIdOnTheChatSocket_Is400_WithEnforcementOnOrOff()
    {
        LogTestStart();

        // /ws?threadId=%20. The value is present and unusable, which is neither of the two cases the
        // route handled: absent (mint a new conversation) or usable (route to it). It reached
        // WebSocketConversationGate's argument guard and faulted the request as a 500 - and did so
        // with Identity:Enforce OFF, where that gate is otherwise a no-op, so a deployment that has
        // authorization turned off still had a route a caller could make fault.
        //
        // Both settings are exercised because the whole point is that the answer does not depend on
        // enforcement: a malformed query is malformed either way, and an input whose handling is
        // decided by an authorization flag is the shape of the original defect.
        // Driven through a real handshake rather than a plain GET: the endpoint refuses a
        // non-WebSocket request with its own 400 before it ever looks at the query, so a GET would
        // report the right status for the wrong reason and pass just as well against the defect.
        //
        // Both configurations reach the route with a caller it will actually serve - anonymous when
        // enforcement is off, credentialled when it is on. An anonymous handshake against an enforcing
        // host is refused by the identity boundary long before the query is parsed, and asserting on
        // THAT would prove nothing about this.
        using (var permissive = NewFactory())
        {
            Func<Task> handshake = () => permissive.ConnectWebSocketAsync(" ");
            var thrown = await handshake.Should().ThrowAsync<InvalidOperationException>();
            AssertRefusedAsMalformed(thrown.Which);
        }

        using (var enforcing = NewFactory(EnforcingSettings(), WithTestPrincipalSource))
        {
            Func<Task> handshake = () => enforcing.ConnectWebSocketAsync(" ", subProtocols: AliceCredential());
            var thrown = await handshake.Should().ThrowAsync<InvalidOperationException>();
            AssertRefusedAsMalformed(thrown.Which);
        }

        // The status travels in the message - a failed handshake surfaces nothing else to the client,
        // which is why these assertions live in this suite at all.
        static void AssertRefusedAsMalformed(InvalidOperationException thrown)
        {
            _ = thrown
                .Message.Should()
                .NotContain(
                    "500",
                    "a blank threadId is a client mistake, and answering it with a fault lets a caller "
                        + "make the host log an unhandled exception at will"
                );
            _ = thrown
                .Message.Should()
                .Contain("400", "the malformed query is what is wrong, and the caller has to be told that");
        }
    }

    [Fact]
    public async Task WithEnforcementOn_TheFocusedSubAgentSocket_IsRefusedToo()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings());

        // /ws/subagent is a SECOND transport on the same prefix, and a boundary that covered only
        // the route someone happened to name would leave it open. It relays a live child agent's
        // transcript, so it discloses exactly the conversation content the REST surface refuses.
        Func<Task> handshake = () => factory.ConnectSubAgentWebSocketAsync("thread-anon", "agent-1");

        _ = await handshake.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task WithEnforcementOn_ACredentialInTheHandshakeSubprotocol_AdmitsTheSocket()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings(), WithTestPrincipalSource);

        // Since #419 the socket also authorizes the CONVERSATION, so the thread has to be Alice's
        // before this test can be about the credential at all.
        await ProvisionOwnedThreadAsync(factory, "thread-authenticated", "dir-a:alice");

        // The decision recorded for #342: the browser WebSocket API admits no custom headers, but it
        // DOES choose the Sec-WebSocket-Protocol list, so the credential travels there and is
        // promoted into Authorization before UseAuthentication - which is what makes /ws resolve its
        // principal through the SAME front doors as REST rather than a second, parallel one.
        using var socket = await factory.ConnectWebSocketAsync("thread-authenticated", subProtocols: AliceCredential());

        _ = socket.State.Should().Be(System.Net.WebSockets.WebSocketState.Open);

        // The server echoes the APPLICATION subprotocol, never the credential one - echoing the
        // credential would hand it back to anything reading the response headers.
        _ = socket.SubProtocol.Should().Be(IdentityMiddleware.WebSocketSubProtocol);

        await socket.CloseAsync(
            System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
            "done",
            CancellationToken.None
        );
    }

    [Fact]
    public async Task WithEnforcementOn_APooledEntryCreatedOverTheSocket_IsOwnedByTheConnectingUser()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings(), WithTestPrincipalSource);
        const string ThreadId = "thread-owned-over-ws";
        await ProvisionOwnedThreadAsync(factory, ThreadId, "dir-a:alice");

        using var socket = await factory.ConnectWebSocketAsync(ThreadId, subProtocols: AliceCredential());

        var pool = factory.Services.GetRequiredService<MultiTurnAgentPool>();

        // The socket resolves at AcceptWebSocketAsync; the pooled entry is created just after, on the
        // connection's own task. Bounded, and loud on timeout - a silent poll would make every
        // assertion below vacuous exactly when the entry was never created.
        await Wait.UntilAsync(
            () => pool.TryGet(ThreadId, out _),
            because: "the WebSocket connection creates the thread's pooled agent",
            timeout: TimeSpan.FromSeconds(20)
        );

        // #399: in the browser the FIRST toucher of a thread is /ws, opened on load before any REST
        // turn. An entry created unowned freezes OwnerUserId to null for the entry's whole life, and
        // EnsurePrincipalMatches returns early when either side is null - so the REST guard added by
        // #302 was dead for every conversation the UI had ever opened a socket on.
        _ = pool.GetAgentOwnerUserId(ThreadId).Should().Be("dir-a:alice");

        // The behavioural half, because the value alone does not prove the guard is armed by it.
        Func<Task> bobsTurn = () => pool.EnsureCurrentAgentAsync(ThreadId, ownerUserId: "dir-b:bob");

        var conflict = await bobsTurn
            .Should()
            .ThrowAsync<PrincipalConflictException>(
                "a second human's turn on a socket-created thread must conflict exactly as it does on a "
                    + "REST-created one"
            );
        _ = conflict.Which.ExistingUserId.Should().Be("dir-a:alice");

        await socket.CloseAsync(
            System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
            "done",
            CancellationToken.None
        );
    }

    /// <summary>
    /// The consequence of #399 that the pool-level assertion cannot show: once a socket-created entry
    /// is OWNED, a second human's handshake on the same thread hits the pool's principal guard during
    /// connection setup. That has to reach the client as a structured refusal - the same shape the
    /// app-id conflict beside it already uses - rather than as an unhandled exception that aborts the
    /// socket and tells the UI nothing.
    /// </summary>
    /// <remarks>
    /// Bob holds an EDITOR grant here, and that is what keeps the test about #399 after #419. Without
    /// a grant his handshake is now refused a layer earlier, by the conversation gate, and the pool
    /// guard below would never be reached - the assertion would still pass, on the wrong mechanism.
    /// The grant is also the only shape in which #399's frame is still reachable at all: two humans
    /// share a live agent exactly when one of them shared the conversation with the other.
    /// </remarks>
    [Fact]
    public async Task WithEnforcementOn_ASecondUsersSocket_IsRefusedTheOwnersLiveAgent()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings(), WithTestPrincipalSource);
        const string ThreadId = "thread-two-humans-one-thread";

        await ProvisionOwnedThreadAsync(factory, ThreadId, "dir-a:alice");
        await GrantEditorAsync(factory, ThreadId, "dir-b:bob");

        using var alicesSocket = await factory.ConnectWebSocketAsync(
            ThreadId,
            subProtocols: CredentialFor("dir-a:alice")
        );

        var pool = factory.Services.GetRequiredService<MultiTurnAgentPool>();
        await Wait.UntilAsync(
            () => pool.TryGet(ThreadId, out _),
            because: "the first connection creates the thread's pooled agent",
            timeout: TimeSpan.FromSeconds(20)
        );

        var bobsSocket = await factory.ConnectWebSocketAsync(ThreadId, subProtocols: CredentialFor("dir-b:bob"));
        await using var bob = new WebSocketTestClient(bobsSocket);

        using var refusal = await bob.WaitForFrameAsync(
            frame =>
                frame.RootElement.TryGetProperty("code", out var code)
                && string.Equals(code.GetString(), "principal_conflict", StringComparison.Ordinal),
            TimeSpan.FromSeconds(20)
        );

        // The refusal is not the whole claim: the owner's agent must still be hers afterwards.
        _ = pool.GetAgentOwnerUserId(ThreadId).Should().Be("dir-a:alice");

        await alicesSocket.CloseAsync(
            System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
            "done",
            CancellationToken.None
        );
    }

    /// <summary>
    /// Asks the real predicate rather than restating the rule, so an edit to the exemption list
    /// cannot agree with a copy of itself.
    /// </summary>
    private static bool IsGuarded(string route) => IdentityMiddleware.IsGuardedApiPath(new PathString("/" + route));

    /// <summary>
    /// Every route the host publishes, normalised the same way <see cref="ApiRoutes"/> normalises
    /// its own - including the WebSocket transports, which <c>app.Map(pattern, Delegate)</c>
    /// publishes as ordinary minimal-API endpoints.
    /// </summary>
    private static IReadOnlyList<string> AllRoutes(E2EWebAppFactory factory) =>
        [
            .. factory
                .Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                .Select(endpoint => (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/'))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(route => route, StringComparer.Ordinal),
        ];

    private static IReadOnlyList<string> ApiRoutes(E2EWebAppFactory factory) =>
        [
            .. factory
                .Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                // TrimStart the leading slash before filtering. A controller route's RawText is
                // "api/..." with no slash, but a minimal-API route mapped as "/api/..." keeps its
                // slash - so a bare StartsWith("api/") silently drops every minimal-API /api route,
                // and this coverage test would never see one land outside the boundary. There are
                // none today; normalising here means adding one cannot hide it.
                .Select(endpoint => (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/'))
                .Where(route => route.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(route => route, StringComparer.Ordinal),
        ];

    private static Dictionary<string, string?> EnforcingSettings() =>
        new(StringComparer.Ordinal)
        {
            ["Identity:Enforce"] = "true",
            ["Identity:DatabasePath"] = Path.Combine(Path.GetTempPath(), $"identity_e2e_{Guid.NewGuid():N}.db"),
            ["Identity:Apps:" + DaemonAppId + ":TenantId"] = DaemonTenant,
            ["Auth:S2SInboundSecret"] = Secret,
            ["LmStreaming:AllowedOrigins:0"] = ClientOrigin,
        };

    /// <summary>
    /// The flags that publish the lifecycle control plane. The callback allow-list is not optional:
    /// the options are validated at wiring time, so a host that enables delivery without one does
    /// not boot.
    /// </summary>
    private static Dictionary<string, string?> LifecycleSettings() =>
        new(StringComparer.Ordinal)
        {
            ["Lifecycle:Delivery:Enabled"] = "true",
            ["Lifecycle:Delivery:AllowedCallbackHosts:0"] = CallbackHost,
            ["Lifecycle:Approval:Enabled"] = "true",
        };

    /// <summary>An enforcing host that also publishes the lifecycle control plane.</summary>
    private static Dictionary<string, string?> LifecycleEnforcingSettings()
    {
        var settings = EnforcingSettings();
        foreach (var (key, value) in LifecycleSettings())
        {
            settings[key] = value;
        }

        return settings;
    }

    /// <summary>
    /// A subscription registration addressed to an allow-listed callback, so nothing but identity
    /// can refuse it. Registration performs no DNS, so the host need not exist.
    /// </summary>
    private static HttpRequestMessage RegistrationRequest(bool daemonHeaders)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/lifecycle/subscriptions", UriKind.Relative))
        {
            Content = new StringContent(
                $$"""{"callback_uri":"https://{{CallbackHost}}/hook"}""",
                Encoding.UTF8,
                "application/json"
            ),
        };

        if (daemonHeaders)
        {
            request.Headers.TryAddWithoutValidation("X-S2S-Auth", Secret);
            request.Headers.TryAddWithoutValidation("X-Sbx-App-Id", DaemonAppId);
            request.Headers.TryAddWithoutValidation("X-Sbx-App-Key", "app-key-value");
        }

        return request;
    }

    /// <summary>
    /// The subprotocol list a signed-in browser offers: the credential, then the application
    /// subprotocol the server is allowed to echo back.
    /// </summary>
    private static string[] AliceCredential() => CredentialFor("dir-a:alice");

    /// <summary>The same list for any user id, so a test can play a second human.</summary>
    private static string[] CredentialFor(string userId) =>
        [IdentityMiddleware.WebSocketCredentialSubProtocolPrefix + userId, IdentityMiddleware.WebSocketSubProtocol];

    /// <summary>
    /// Registers a front door that turns <c>Authorization: Bearer &lt;userId&gt;</c> into an end-user
    /// principal. Deliberately the REAL extension point <see cref="IdentityMiddleware"/> consults
    /// (<see cref="IRequestPrincipalSource"/>) rather than a hand-placed stash, so a test credential
    /// travels the same path a token does: header -> front door -> principal.
    /// </summary>
    private static void WithTestPrincipalSource(IServiceCollection services) =>
        services.AddSingleton<IRequestPrincipalSource, BearerUserPrincipalSource>();

    /// <summary>
    /// Wires BOTH halves of what a real signed-in person looks like to the host: an authentication
    /// scheme that populates <c>HttpContext.User</c> before <c>IdentityMiddleware</c> runs (what a
    /// JWT bearer handler does), and the front door that turns the same credential into an
    /// interactive <see cref="Principal"/> (what <c>OnTokenValidated</c> does). Only both together
    /// reproduce #433: with the principal alone the bridge would run and the caller would carry no
    /// name identifier; with the scheme alone the identity boundary would refuse before any
    /// controller ran.
    /// </summary>
    /// <remarks>
    /// The scheme is registered under the bearer name because the sample creates that scheme but
    /// registers no handler for it unless an Entra client id is configured, which no test sets.
    /// </remarks>
    private static void WithSignedInHumanBearerScheme(IServiceCollection services)
    {
        WithTestPrincipalSource(services);
        _ = services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, SignedInHumanHandler>(
                JwtBearerDefaults.AuthenticationScheme,
                _ => { }
            );
    }

    /// <summary>
    /// Authenticates <c>Authorization: Bearer &lt;subject&gt;</c> into the claims an Entra access token
    /// produces once inbound claim mapping has run: <c>sub</c> on
    /// <see cref="ClaimTypes.NameIdentifier"/>, plus a display name. Deliberately no app-id claim: this
    /// host maps none inbound and mints it in one place only, downstream of establishing the caller is
    /// an app, so a real token reaching a real handler here produces exactly this claim set. The
    /// handler is what decides that, not the token, which is why the fix is the single stamping site
    /// rather than anything about token contents.
    /// </summary>
    private sealed class SignedInHumanHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var subject = header["Bearer ".Length..].Trim();
            if (subject.Length == 0)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, subject), new Claim(ClaimTypes.Name, subject)],
                JwtBearerDefaults.AuthenticationScheme
            );

            return Task.FromResult(
                AuthenticateResult.Success(
                    new AuthenticationTicket(new ClaimsPrincipal(identity), JwtBearerDefaults.AuthenticationScheme)
                )
            );
        }
    }

    private sealed class BearerUserPrincipalSource : IRequestPrincipalSource
    {
        public ValueTask<PrincipalResolution?> ResolveAsync(HttpContext context, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(context);

            var header = context.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                return ValueTask.FromResult<PrincipalResolution?>(null);
            }

            var userId = header["Bearer ".Length..].Trim();
            if (userId.Length == 0)
            {
                return ValueTask.FromResult<PrincipalResolution?>(null);
            }

            return ValueTask.FromResult<PrincipalResolution?>(
                PrincipalResolution.Success(
                    new Principal
                    {
                        TenantId = DaemonTenant,
                        Actor = new PrincipalRef(PrincipalKind.EndUser, userId),
                        Source = PrincipalSource.Interactive,
                    }
                )
            );
        }
    }

    /// <summary>
    /// Writes the metadata row <c>POST /api/conversations</c> would write, so a WebSocket test can own
    /// a conversation without driving provisioning through the REST surface it is not testing. Needed
    /// since #419: <c>/ws</c> now authorizes the conversation, and an unstamped thread id is refused
    /// exactly as another tenant's is.
    /// </summary>
    private static Task ProvisionOwnedThreadAsync(E2EWebAppFactory factory, string threadId, string userId)
    {
        var store = factory.Services.GetRequiredService<IConversationStore>();
        return store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TenantId = DaemonTenant,
                OwnerUserId = userId,
                Visibility = Visibility.Private,
            }
        );
    }

    /// <summary>Shares a conversation with a second human as an editor (write, not just read).</summary>
    private static Task GrantEditorAsync(E2EWebAppFactory factory, string threadId, string subjectId)
    {
        var grants = factory.Services.GetRequiredService<IResourceGrantStore>();
        return grants.GrantAsync(
            new ResourceGrant
            {
                TenantId = DaemonTenant,
                Resource = ConversationAuthorizer.ConversationRef(threadId),
                SubjectId = subjectId,
                Role = GrantRole.Editor,
                GrantedBy = "dir-a:alice",
                GrantedAt = DateTimeOffset.UtcNow,
            }
        );
    }

    private static E2EWebAppFactory NewFactory(
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null
    )
    {
        // Any scripted handler works - nothing here creates an agent.
        var responder = ScriptedSseResponder.New().ForRole("noop", _ => true).Turn(t => t.Text("ok")).Build();

        return new E2EWebAppFactory(
            "test",
            new ScriptedBuilder(responder.AsAnthropicHandler()),
            settings,
            configureServices
        );
    }
}
