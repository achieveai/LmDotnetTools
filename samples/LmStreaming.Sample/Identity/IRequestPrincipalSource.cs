namespace LmStreaming.Sample.Identity;

/// <summary>
/// One front door that can establish a <see cref="PrincipalResolution"/> for a request, consulted
/// by <see cref="IdentityMiddleware"/> before it decides whether to refuse.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of an ORDERING bug, not for extensibility (#345). The interactive door
/// stashes its resolution from the JWT bearer handler, which runs in
/// <c>UseAuthentication</c> - upstream of the identity middleware. Every other door in this
/// codebase was a filter, and a filter runs at endpoint execution, which is DOWNSTREAM. A service
/// caller that authenticates correctly with <c>X-S2S-Auth</c> was therefore refused by the
/// middleware before its own guard ever ran: fail-closed, but a total outage for every non-browser
/// caller the moment <c>Identity:Enforce</c> flipped.
/// </para>
/// <para>
/// A source returns <see langword="null"/> to mean "this is not my kind of request" - not "refused".
/// Returning a rejection is a positive statement that this door recognised the caller and turned
/// them away, and it stops the search: no later source may promote a caller a earlier one refused.
/// </para>
/// </remarks>
public interface IRequestPrincipalSource
{
    /// <summary>
    /// Tries to establish a principal for <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The request being resolved.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A resolution - successful or refused - when this door recognises the request, otherwise
    /// <see langword="null"/>.
    /// </returns>
    ValueTask<PrincipalResolution?> ResolveAsync(HttpContext context, CancellationToken ct);
}
