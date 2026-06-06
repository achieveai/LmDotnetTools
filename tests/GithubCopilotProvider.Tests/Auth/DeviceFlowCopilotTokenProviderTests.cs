using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Auth;

public sealed class DeviceFlowCopilotTokenProviderTests
{
    [Fact]
    public async Task Completes_device_flow_and_caches_token_in_memory()
    {
        var deviceCodeRequests = 0;
        var tokenRequests = 0;

        var handler = new FakeHttpMessageHandler(
            (request, _) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url.Contains("login/device/code", StringComparison.Ordinal))
                {
                    deviceCodeRequests++;
                    return Task.FromResult(
                        Json(
                            """{"device_code":"dc","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":0}"""
                        )
                    );
                }

                if (url.Contains("login/oauth/access_token", StringComparison.Ordinal))
                {
                    tokenRequests++;
                    // Pending on the first poll, success on the second — exercises the poll loop.
                    return Task.FromResult(
                        tokenRequests == 1
                            ? Json("""{"error":"authorization_pending"}""")
                            : Json("""{"access_token":"gho_device"}""")
                    );
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        );

        var presented = 0;
        var provider = new DeviceFlowCopilotTokenProvider(
            httpClient: new HttpClient(handler),
            present: _ => presented++,
            cacheToDisk: false
        );

        var token = await provider.GetTokenAsync();
        token.Should().Be("gho_device");
        presented.Should().Be(1);
        deviceCodeRequests.Should().Be(1);
        tokenRequests.Should().Be(2);

        // Second call is served from the in-memory cache (no further HTTP).
        var again = await provider.GetTokenAsync();
        again.Should().Be("gho_device");
        deviceCodeRequests.Should().Be(1);
        tokenRequests.Should().Be(2);
    }

    [Fact]
    public async Task Throws_on_access_denied()
    {
        var handler = new FakeHttpMessageHandler(
            (request, _) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                return Task.FromResult(
                    url.Contains("login/device/code", StringComparison.Ordinal)
                        ? Json(
                            """{"device_code":"dc","user_code":"X","verification_uri":"https://github.com/login/device","expires_in":900,"interval":0}"""
                        )
                        : Json("""{"error":"access_denied"}""")
                );
            }
        );

        var provider = new DeviceFlowCopilotTokenProvider(
            httpClient: new HttpClient(handler),
            present: _ => { },
            cacheToDisk: false
        );

        var act = async () => await provider.GetTokenAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
