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

    private static PredefinedKeyRegistry NewRegistry(string dir) =>
        new(dir, new NoopStore(), new HttpClient(), NullLoggerFactory.Instance);

    private static PredefinedKeyEntry Custom(string id, string host = "api.example.com") => new()
    {
        Id = id,
        Host = host,
        Kind = PredefinedKeyKind.CustomHeaders,
        Headers = [new PredefinedHeader("X-Key", "v")],
    };

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
}
