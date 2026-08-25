using AchieveAi.LmDotnetTools.LmCore.Identity;

namespace LmStreaming.Sample.Identity;

/// <summary>
/// The outcome of trying to turn a validated token into a <see cref="Principal"/>: either a
/// principal, or a refusal carrying the status code and stable code the caller must answer with.
/// </summary>
/// <remarks>
/// A refusal is modelled as a value rather than as an exception or an authentication failure on
/// purpose. Failing the authentication handler would produce a <c>401</c> challenge, and a
/// <c>401</c> is what makes a browser client sign in again - which cannot fix a missing tenant, so
/// it loops forever. That loop is the specific failure mode this type exists to avoid.
/// </remarks>
public sealed record PrincipalResolution
{
    private PrincipalResolution()
    {
    }

    /// <summary>The resolved principal, or null when <see cref="IsRejected"/>.</summary>
    public Principal? Principal { get; private init; }

    /// <summary>Stable refusal code, e.g. <c>tenant_not_provisioned</c>. Null on success.</summary>
    public string? Code { get; private init; }

    /// <summary>HTTP status the refusal must be answered with. Zero on success.</summary>
    public int StatusCode { get; private init; }

    /// <summary>Whether this resolution refused the sign-in.</summary>
    public bool IsRejected => Code is not null;

    /// <summary>A successful resolution.</summary>
    /// <param name="principal">The principal that was constructed.</param>
    public static PrincipalResolution Success(Principal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new PrincipalResolution { Principal = principal };
    }

    /// <summary>
    /// A refusal. The status code is deliberately a parameter rather than a constant: a token that
    /// could not be established as genuine is <c>401</c> and may be retried, while a genuine token
    /// naming an identity that is not entitled to be here is <c>403</c> and retrying changes
    /// nothing.
    /// </summary>
    /// <param name="code">Stable refusal code, also written to the audit record.</param>
    /// <param name="statusCode">HTTP status to answer with.</param>
    public static PrincipalResolution Reject(string code, int statusCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new PrincipalResolution { Code = code, StatusCode = statusCode };
    }

    /// <summary>The token names an Entra directory that is not a provisioned customer.</summary>
    public const string TenantNotProvisioned = "tenant_not_provisioned";

    /// <summary>The token names a provisioned but suspended tenant.</summary>
    public const string TenantSuspended = "tenant_suspended";

    /// <summary>The token validated but carried no usable <c>tid</c>/<c>oid</c> pair.</summary>
    public const string InvalidToken = "invalid_token";
}
