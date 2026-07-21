namespace LmStreaming.Sample.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="SessionSecretStore"/>: the per-session, disk-persisted secret store
/// that replaced the single process-wide <c>AuthSharedSecret</c>. Covers the guarantees the redesign
/// exists for — restart survival (a fresh store instance over the same directory still matches) and
/// cross-session isolation (one session's secret never matches another's) — plus the rejection paths
/// <c>AuthWebhookController</c>/<c>ContextDiscoveryController</c> depend on.
/// </summary>
public sealed class SessionSecretStoreTests
{
    private static SessionSecretStore NewStore(string? baseDirectory = null) => new(
        baseDirectory ?? Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
        NullLogger<SessionSecretStore>.Instance);

    [Fact]
    public async Task SaveThenMatch_WithCorrectSecret_ReturnsTrue()
    {
        var store = NewStore();

        await store.SaveAsync("session-a", "secret-a");

        (await store.MatchesAsync("session-a", "secret-a")).Should().BeTrue();
    }

    [Fact]
    public async Task Match_WithWrongSecret_ReturnsFalse()
    {
        var store = NewStore();

        await store.SaveAsync("session-a", "secret-a");

        (await store.MatchesAsync("session-a", "not-secret-a")).Should().BeFalse();
    }

    [Fact]
    public async Task DifferentSessionIds_NeverMatchEachOthersSecret()
    {
        var store = NewStore();

        await store.SaveAsync("session-a", "secret-a");
        await store.SaveAsync("session-b", "secret-b");

        (await store.MatchesAsync("session-b", "secret-a")).Should().BeFalse();
        (await store.MatchesAsync("session-a", "secret-b")).Should().BeFalse();

        // Each session's own secret still matches — isolation didn't break the happy path either.
        (await store.MatchesAsync("session-a", "secret-a")).Should().BeTrue();
        (await store.MatchesAsync("session-b", "secret-b")).Should().BeTrue();
    }

    [Fact]
    public async Task Match_ForUnknownSessionId_ReturnsFalse()
    {
        var store = NewStore();

        (await store.MatchesAsync("never-created", "anything")).Should().BeFalse();
    }

    // Ids that the OLD lossy sanitizer collapsed to the SAME file name: it lowercased and dropped every
    // character outside [a-z0-9_-]. "Session-A"/"session-a" collided on case; "session.a"/"sessiona"
    // collided because "." was stripped. A collision let a later session read/overwrite another's secret.
    [Theory]
    [InlineData("Session-A", "session-a")]
    [InlineData("session.a", "sessiona")]
    public async Task PreviouslyCollidingSessionIds_NowMapToDistinctSecrets(string idOne, string idTwo)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N"));
        var store = NewStore(directory);

        await store.SaveAsync(idOne, "secret-one");
        await store.SaveAsync(idTwo, "secret-two");

        // Neither id can read the other's secret (the isolation the collision used to break)...
        (await store.MatchesAsync(idOne, "secret-two")).Should().BeFalse();
        (await store.MatchesAsync(idTwo, "secret-one")).Should().BeFalse();

        // ...and saving idTwo did NOT overwrite idOne's file — each still matches its own secret.
        (await store.MatchesAsync(idOne, "secret-one")).Should().BeTrue();
        (await store.MatchesAsync(idTwo, "secret-two")).Should().BeTrue();

        // Two distinct ids ⇒ two distinct files on disk.
        Directory.GetFiles(directory, "*.secret").Should().HaveCount(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Match_WithMissingPresentedValue_ReturnsFalse(string? presented)
    {
        var store = NewStore();

        await store.SaveAsync("session-a", "secret-a");

        (await store.MatchesAsync("session-a", presented)).Should().BeFalse();
    }

    [Fact]
    public async Task NewStoreInstance_OverSameDirectory_StillMatches_SurvivingARestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N"));
        var firstProcessStore = NewStore(directory);
        await firstProcessStore.SaveAsync("session-a", "secret-a");

        // A fresh instance over the same directory simulates the app restarting — no shared
        // in-memory state, only the file on disk.
        var secondProcessStore = NewStore(directory);

        (await secondProcessStore.MatchesAsync("session-a", "secret-a")).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveAsync_ThenMatch_ReturnsFalse()
    {
        var store = NewStore();

        await store.SaveAsync("session-a", "secret-a");
        await store.RemoveAsync("session-a");

        (await store.MatchesAsync("session-a", "secret-a")).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_OverwritesPreviousSecret_ForSameSessionId()
    {
        var store = NewStore();

        await store.SaveAsync("session-a", "secret-a");
        await store.SaveAsync("session-a", "secret-a-rotated");

        (await store.MatchesAsync("session-a", "secret-a")).Should().BeFalse();
        (await store.MatchesAsync("session-a", "secret-a-rotated")).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SaveAsync_WithInvalidSessionId_Throws(string? sessionId)
    {
        var store = NewStore();

        var act = async () => await store.SaveAsync(sessionId!, "secret-a");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task SaveAsync_WithInvalidSecret_Throws(string? secret)
    {
        var store = NewStore();

        var act = async () => await store.SaveAsync("session-a", secret!);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Match_WithInvalidSessionId_ReturnsFalse_RatherThanThrowing(string? sessionId)
    {
        var store = NewStore();

        (await store.MatchesAsync(sessionId!, "anything")).Should().BeFalse();
    }
}
