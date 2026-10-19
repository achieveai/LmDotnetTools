using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Hosts the sample's real controllers behind real routing, so <see cref="OperatorSecretAuthAttribute"/> runs as
/// it does in production. The guard is an action filter: calling a controller method directly skips it.
/// </summary>
internal static class OperatorGateTestServer
{
    public const string Secret = "operator-secret-value";

    /// <summary>Starts a host with <paramref name="services"/> registered and the operator secret set, or unset when null.</summary>
    public static async Task<IHost> StartAsync(string? secret, Action<IServiceCollection> services)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webHost =>
                webHost
                    .UseTestServer()
                    .ConfigureAppConfiguration(config =>
                    {
                        if (secret is not null)
                        {
                            _ = config.AddInMemoryCollection(
                                new Dictionary<string, string?>
                                {
                                    [OperatorSecretAuthAttribute.SecretConfigKey] = secret,
                                }
                            );
                        }
                    })
                    .ConfigureServices(s =>
                    {
                        services(s);
                        _ = s.AddControllers().AddApplicationPart(typeof(OperatorSecretAuthAttribute).Assembly);
                    })
                    .Configure(app =>
                    {
                        _ = app.UseRouting();
                        _ = app.UseEndpoints(endpoints => endpoints.MapControllers());
                    })
            )
            .StartAsync();
    }

    /// <summary>Sends <paramref name="method"/> to <paramref name="path"/>, presenting <paramref name="presentedSecret"/> when not null.</summary>
    public static Task<HttpResponseMessage> SendAsync(
        IHost host,
        HttpMethod method,
        string path,
        string? presentedSecret,
        object? body = null
    )
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (presentedSecret is not null)
        {
            request.Headers.Add(OperatorSecretAuthAttribute.HeaderName, presentedSecret);
        }

        if (body is not null)
        {
            request.Content = System.Net.Http.Json.JsonContent.Create(body);
        }

        return host.GetTestClient().SendAsync(request);
    }
}
