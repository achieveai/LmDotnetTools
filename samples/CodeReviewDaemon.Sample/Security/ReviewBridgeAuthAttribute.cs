using System.Security.Cryptography;
using System.Text;
using CodeReviewDaemon.Sample.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CodeReviewDaemon.Sample.Security;

/// <summary>Authenticates the private review bridge without leaking why a credential was rejected.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class ReviewBridgeAuthAttribute : Attribute, IAuthorizationFilter
{
    internal const string HeaderName = "X-Review-Bridge-Auth";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<CodeReviewDaemonOptions>();
        var presented = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!Matches(presented, options.ReviewBridgeSecret))
        {
            context.Result = new UnauthorizedResult();
        }
    }

    private static bool Matches(string presented, string expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }
}
