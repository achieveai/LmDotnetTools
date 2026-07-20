namespace LmStreaming.Sample.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="PredefinedKeyRegistry"/> — runtime CRUD, disk persistence (reload), and
/// provider resolution. Uses a temp directory and a no-op token store (no network).
/// </summary>
public sealed class PredefinedKeyRegistryTests
{
    private sealed class NoopStore : IOAuthTokenStore
    {
        public Task<OAuthTokenRecord?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<OAuthTokenRecord?>(null);

        public Task SaveAsync(OAuthTokenRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string provider, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InMemoryStore : IOAuthTokenStore
    {
        private readonly Dictionary<string, OAuthTokenRecord> _map = new(StringComparer.Ordinal);

        public bool ThrowOnRemove { get; init; }

        public Task<OAuthTokenRecord?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(_map.TryGetValue(provider, out var r) ? r : null);

        public Task SaveAsync(OAuthTokenRecord record, CancellationToken ct = default)
        {
            _map[record.Provider] = record;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string provider, CancellationToken ct = default)
        {
            if (ThrowOnRemove)
            {
                throw new IOException("simulated token-store cleanup failure");
            }

            _ = _map.Remove(provider);
            return Task.CompletedTask;
        }
    }

    private static PredefinedKeyRegistry NewRegistry(string dir, IOAuthTokenStore? store = null) =>
        new(dir, store ?? new NoopStore(), new HttpClient(), NullLoggerFactory.Instance);

    private static PredefinedKeyEntry Custom(string id, string host = "api.example.com") => new()
    {
        Id = id,
        Host = host,
        Kind = PredefinedKeyKind.CustomHeaders,
        Headers = [new PredefinedHeader("X-Key", "v")],
    };

    private static PredefinedKeyEntry Refresh(string id, string host = "api.example.com", string refreshToken = "rt0") => new()
    {
        Id = id,
        Host = host,
        Kind = PredefinedKeyKind.RefreshToken,
        TokenEndpoint = "https://token.example/oauth/token",
        ClientId = "cid",
        RefreshToken = refreshToken,
    };

    private static OAuthTokenRecord Minted(string providerId) =>
        new(providerId, null, "rotated-rt", "at", DateTimeOffset.UtcNow.AddHours(1), []);

    [Fact]
    public async Task Upsert_creates_resolves_and_persists_across_reload()
    {
        var dir = Directory.CreateTempSubdirectory("egr-reg");
        try
        {
            var registry = NewRegistry(dir.FullName);
            await registry.UpsertAsync(Custom("e1"));

            registry.TryResolve("predefined-e1").Should().NotBeNull();
            registry.Entries.Should().ContainSingle(e => e.Id == "e1");

            // A fresh registry over the same dir reloads the persisted entry.
            NewRegistry(dir.FullName).Entries.Should().ContainSingle(e => e.Id == "e1");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Update_keeps_the_same_provider_instance()
    {
        var dir = Directory.CreateTempSubdirectory("egr-reg");
        try
        {
            var registry = NewRegistry(dir.FullName);
            await registry.UpsertAsync(Custom("e1", "a.example.com"));
            var first = registry.TryResolve("predefined-e1");

            await registry.UpsertAsync(Custom("e1", "b.example.com"));
            var second = registry.TryResolve("predefined-e1");

            second.Should().BeSameAs(first); // updated in place, not replaced
            registry.Entries.Single().Host.Should().Be("b.example.com");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Remove_deletes_and_persists()
    {
        var dir = Directory.CreateTempSubdirectory("egr-reg");
        try
        {
            var registry = NewRegistry(dir.FullName);
            await registry.UpsertAsync(Custom("e1"));

            (await registry.RemoveAsync("e1")).Should().BeTrue();
            registry.TryResolve("predefined-e1").Should().BeNull();
            NewRegistry(dir.FullName).Entries.Should().BeEmpty();

            (await registry.RemoveAsync("missing")).Should().BeFalse();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryResolve_ignores_non_predefined_ids()
    {
        var dir = Directory.CreateTempSubdirectory("egr-reg");
        try
        {
            var registry = NewRegistry(dir.FullName);
            registry.TryResolve("github").Should().BeNull();
            registry.TryResolve(null).Should().BeNull();
            registry.TryResolve("predefined-unknown").Should().BeNull();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Update_changing_credential_invalidates_the_persisted_token()
    {
        var dir = Directory.CreateTempSubdirectory("egr-reg");
        try
        {
            var store = new InMemoryStore();
            var registry = NewRegistry(dir.FullName, store);
            await registry.UpsertAsync(Refresh("e1"));
            await store.SaveAsync(Minted("predefined-e1"));

            await registry.UpsertAsync(Refresh("e1", refreshToken: "new-rt")); // credential change

            (await store.GetAsync("predefined-e1")).Should().BeNull();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Update_preserving_credential_retains_the_persisted_rotated_token()
    {
        var dir = Directory.CreateTempSubdirectory("egr-reg");
        try
        {
            var store = new InMemoryStore();
            var registry = NewRegistry(dir.FullName, store);
            await registry.UpsertAsync(Refresh("e1"));
            await store.SaveAsync(Minted("predefined-e1"));

            await registry.UpsertAsync(Refresh("e1", host: "api2.example.com")); // host-only edit

            (await store.GetAsync("predefined-e1")).Should().NotBeNull();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Remove_cleans_up_the_persisted_token()
    {
        var dir = Directory.CreateTempSubdirectory("egr-reg");
        try
        {
            var store = new InMemoryStore();
            var registry = NewRegistry(dir.FullName, store);
            await registry.UpsertAsync(Refresh("e1"));
            await store.SaveAsync(Minted("predefined-e1"));

            (await registry.RemoveAsync("e1")).Should().BeTrue();

            (await store.GetAsync("predefined-e1")).Should().BeNull();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Credential_change_aborts_when_token_invalidation_fails_but_delete_stays_best_effort()
    {
        var dir = Directory.CreateTempSubdirectory("egr-reg");
        try
        {
            var store = new InMemoryStore { ThrowOnRemove = true };
            var registry = NewRegistry(dir.FullName, store);
            await registry.UpsertAsync(Refresh("e1")); // create: no existing entry → no token invalidation

            // A credential-change upsert must FAIL (reliably) if the stale token can't be invalidated,
            // rather than commit the new definition while the old token stays reloadable.
            await FluentActions.Awaiting(() => registry.UpsertAsync(Refresh("e1", refreshToken: "x")))
                .Should().ThrowAsync<IOException>();

            // Delete cleanup stays best-effort — a token-store failure does not fail the delete.
            (await registry.RemoveAsync("e1")).Should().BeTrue();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
