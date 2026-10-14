using System.Net;
using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>Disposable browser identity for the dedicated local HTTPS E2E host only.</summary>
internal sealed class MiniAppLocalTestPrincipalSource : IRequestPrincipalSource
{
    internal const string CookieName = "__Host-MiniAppLocalTest";
    private readonly byte[] _expectedHash;
    private readonly int _port;

    public MiniAppLocalTestPrincipalSource(IHostEnvironment environment, string secret, int port)
    {
        if (
            !environment.IsEnvironment("MiniAppLocalTest")
            || string.IsNullOrWhiteSpace(secret)
            || port is < 1 or > 65535
        )
            throw new InvalidOperationException("Local Mini App identity requires its dedicated test environment.");
        _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        _port = port;
    }

    public ValueTask<PrincipalResolution?> ResolveAsync(HttpContext context, CancellationToken ct)
    {
        var request = context.Request;
        if (
            !request.IsHttps
            || !string.Equals(request.Host.Host, "site.lvh.me", StringComparison.OrdinalIgnoreCase)
            || request.Host.Port != _port
            || context.Connection.LocalIpAddress is null
            || !IPAddress.IsLoopback(context.Connection.LocalIpAddress)
            || context.Connection.RemoteIpAddress is null
            || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress)
            || !request.Cookies.TryGetValue(CookieName, out var actual)
            || actual.Length > 256
        )
            return ValueTask.FromResult<PrincipalResolution?>(null);

        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(actual));
        if (!CryptographicOperations.FixedTimeEquals(_expectedHash, actualHash))
            return ValueTask.FromResult<PrincipalResolution?>(null);

        return ValueTask.FromResult<PrincipalResolution?>(
            PrincipalResolution.Success(
                new Principal
                {
                    TenantId = "tnt_miniapp_local_test",
                    Actor = new PrincipalRef(PrincipalKind.EndUser, "miniapp-local-browser"),
                    Source = PrincipalSource.Interactive,
                }
            )
        );
    }
}
