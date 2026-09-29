using System.Collections.Concurrent;
using System.Security.Cryptography;
using AchieveAi.LmDotnetTools.LmCore.Identity;

namespace LmStreaming.Sample.SandboxApps;

public sealed record SandboxAppLaunch(string Host, string Ticket);

public sealed record SandboxAppGrant(
    string Host,
    string ThreadId,
    string WorkspaceId,
    SandboxAppDefinition App,
    Principal Principal,
    string Cookie,
    string CsrfToken,
    DateTimeOffset ExpiresAt
);

/// <summary>Short-lived, one-use launch tickets and host-bound app grants.</summary>
public sealed class SandboxAppInstanceStore(TimeProvider clock)
{
    private const int MaxOutstandingCredentials = 4096;

    private sealed record TicketRecord(
        string Host,
        string ThreadId,
        string WorkspaceId,
        SandboxAppDefinition App,
        Principal Principal,
        DateTimeOffset TicketExpiresAt,
        DateTimeOffset GrantExpiresAt
    );

    private readonly ConcurrentDictionary<string, TicketRecord> _tickets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SandboxAppGrant> _grants = new(StringComparer.Ordinal);

    public SandboxAppLaunch Issue(
        string threadId,
        string workspaceId,
        SandboxAppDefinition app,
        Principal principal,
        string appDomain,
        DateTimeOffset expiresAt
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(principal);
        if (expiresAt <= clock.GetUtcNow())
            throw new ArgumentOutOfRangeException(nameof(expiresAt));
        if (_tickets.Count + _grants.Count >= MaxOutstandingCredentials)
        {
            RemoveExpired();
            if (_tickets.Count + _grants.Count >= MaxOutstandingCredentials)
                throw new InvalidOperationException("Sandbox app launch capacity is exhausted.");
        }

        var host = $"{RandomToken(16).ToLowerInvariant()}.{appDomain}";
        var ticket = RandomToken(32);
        var ticketExpiry = clock.GetUtcNow().AddMinutes(1);
        if (ticketExpiry > expiresAt)
            ticketExpiry = expiresAt;
        _tickets[ticket] = new TicketRecord(host, threadId, workspaceId, app, principal, ticketExpiry, expiresAt);
        return new SandboxAppLaunch(host, ticket);
    }

    public SandboxAppGrant? Exchange(string ticket, string requestHost)
    {
        if (
            !_tickets.TryGetValue(ticket, out var candidate)
            || !string.Equals(candidate.Host, requestHost, StringComparison.OrdinalIgnoreCase)
        )
            return null;
        if (!_tickets.TryRemove(ticket, out var record) || record.TicketExpiresAt <= clock.GetUtcNow())
            return null;
        var grant = new SandboxAppGrant(
            record.Host,
            record.ThreadId,
            record.WorkspaceId,
            record.App,
            record.Principal,
            RandomToken(32),
            RandomToken(32),
            record.GrantExpiresAt
        );
        _grants[grant.Cookie] = grant;
        return grant;
    }

    public SandboxAppGrant? Authenticate(string? cookie, string requestHost)
    {
        if (string.IsNullOrWhiteSpace(cookie) || !_grants.TryGetValue(cookie, out var grant))
            return null;
        if (grant.ExpiresAt <= clock.GetUtcNow())
        {
            _grants.TryRemove(cookie, out _);
            return null;
        }
        return string.Equals(grant.Host, requestHost, StringComparison.OrdinalIgnoreCase) ? grant : null;
    }

    private static string RandomToken(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));

    private void RemoveExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var (ticket, record) in _tickets)
            if (record.TicketExpiresAt <= now)
                _tickets.TryRemove(ticket, out _);
        foreach (var (cookie, grant) in _grants)
            if (grant.ExpiresAt <= now)
                _grants.TryRemove(cookie, out _);
    }
}
