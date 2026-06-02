using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using LmStreaming.Sample.Services.Auth;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Unit tests for <see cref="FileOAuthTokenStore"/> — the gitignored, file-per-provider refresh-token
/// store. Ungated (no gateway, no network), so they run in CI always. Progress is logged via
/// <see cref="LoggingTestBase"/> to the shared <c>.logs/tests/tests.jsonl</c> file. SECURITY: tests
/// never log token values — only field names / lengths / non-secret metadata.
/// </summary>
public sealed class FileOAuthTokenStoreTests : LoggingTestBase
{
    public FileOAuthTokenStoreTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private FileOAuthTokenStore NewStore(string dir)
    {
        Logger.LogInformation("Creating FileOAuthTokenStore rooted at {Dir}", dir);
        return new FileOAuthTokenStore(dir, LoggerFactory.CreateLogger<FileOAuthTokenStore>());
    }

    private static OAuthTokenRecord SampleRecord(
        string provider = "github",
        string refresh = "refresh-abc",
        string? access = "access-xyz") =>
        new(
            Provider: provider,
            Account: "octocat",
            RefreshToken: refresh,
            AccessToken: access,
            AccessTokenExpiresAtUtc: new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Scopes: ["repo", "read:org"]);

    [Fact]
    public async Task Save_then_Get_round_trips_all_fields()
    {
        LogTestStart();
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        var record = SampleRecord();

        await store.SaveAsync(record);
        var loaded = await store.GetAsync("github");
        Logger.LogInformation(
            "Round-trip loaded provider={Provider}, account={Account}, scopes=[{Scopes}] (token values not logged)",
            loaded?.Provider,
            loaded?.Account,
            loaded is null ? string.Empty : string.Join(", ", loaded.Scopes));

        loaded.Should().NotBeNull();
        loaded!.Provider.Should().Be(record.Provider);
        loaded.Account.Should().Be(record.Account);
        loaded.RefreshToken.Should().Be(record.RefreshToken);
        loaded.AccessToken.Should().Be(record.AccessToken);
        loaded.AccessTokenExpiresAtUtc.Should().Be(record.AccessTokenExpiresAtUtc);
        loaded.Scopes.Should().BeEquivalentTo(record.Scopes);
        LogTestEnd();
    }

    [Fact]
    public async Task Get_missing_provider_returns_null()
    {
        LogTestStart();
        using var temp = new TempDir();
        var store = NewStore(temp.Path);

        var loaded = await store.GetAsync("never-saved");
        Logger.LogInformation("Get for unsaved provider returned null={IsNull}", loaded is null);
        loaded.Should().BeNull();
        LogTestEnd();
    }

    [Fact]
    public async Task Save_overwrites_existing_record()
    {
        LogTestStart();
        using var temp = new TempDir();
        var store = NewStore(temp.Path);

        await store.SaveAsync(SampleRecord(refresh: "first", access: "first-access"));
        await store.SaveAsync(SampleRecord(refresh: "second", access: "second-access"));

        var loaded = await store.GetAsync("github");
        Logger.LogInformation("After overwrite, refresh/access token updated (values not logged)");
        loaded!.RefreshToken.Should().Be("second");
        loaded.AccessToken.Should().Be("second-access");
        LogTestEnd();
    }

    [Fact]
    public async Task Remove_deletes_the_record()
    {
        LogTestStart();
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        await store.SaveAsync(SampleRecord());

        await store.RemoveAsync("github");
        var loaded = await store.GetAsync("github");
        Logger.LogInformation("After remove, Get returned null={IsNull}", loaded is null);

        loaded.Should().BeNull();
        LogTestEnd();
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "oauth-store-test-" + Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
