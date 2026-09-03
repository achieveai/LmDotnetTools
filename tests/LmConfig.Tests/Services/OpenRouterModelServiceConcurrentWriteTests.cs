using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace AchieveAi.LmDotnetTools.LmConfig.Tests.Services;

/// <summary>
/// Covers <c>OpenRouterModelService</c>'s cache write-temp-then-rename path against the two hazards its
/// <c>_cacheSemaphore</c> cannot cover. The semaphore is a per-INSTANCE <see cref="SemaphoreSlim"/>: it
/// serializes writers inside one service object and nothing between two services over one cache file, let
/// alone across processes — and the default cache path is a fixed, machine-wide
/// <c>%TEMP%/LmDotnetTools/openrouter-cache.json</c>, so EVERY process using the default shares one target.
/// <para>
/// What makes this site worth pinning separately is that the failure is SILENT. <c>GetModelConfigsAsync</c>
/// ends in a blanket <c>catch (Exception)</c> that routes to <c>HandleNetworkFailure</c>, so a failed cache
/// save is not surfaced to the caller at all: it degrades to a stale cache, or to an EMPTY model list when
/// there is none. Both tests therefore assert on which models come back rather than merely that nothing
/// threw — a "does not throw" assertion here would pass against the defect.
/// </para>
/// <para>
/// Both tests hold a REAL handle rather than racing threads and hoping. A race-based test for a window this
/// narrow goes green on a fast box whether or not the defect is present, which is precisely the failure mode
/// a concurrency fix must not be verified by. This mirrors
/// <c>LmMultiTurn.Tests.Persistence.FileConversationStoreConcurrentWriteTests</c>.
/// </para>
/// </summary>
public sealed class OpenRouterModelServiceConcurrentWriteTests : IDisposable
{
    private const string FreshModelSlug = "fresh-model";
    private const string StaleModelSlug = "stale-model";

    private readonly Mock<HttpMessageHandler> _handler = new();
    private readonly HttpClient _httpClient;
    private readonly string _root;
    private readonly string _cacheFile;

    public OpenRouterModelServiceConcurrentWriteTests()
    {
        _httpClient = new HttpClient(_handler.Object);
        _root = Path.Combine(Path.GetTempPath(), $"OpenRouterConcurrentWrite_{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(_root);
        _cacheFile = Path.Combine(_root, "openrouter-cache.json");
        SetupMockHttpResponses();
    }

    public void Dispose()
    {
        _httpClient.Dispose();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Teardown of a temp directory must never turn a green run red.
        }
    }

    /// <summary>
    /// Pins the staging-name collision. Holding <c>openrouter-cache.json.tmp</c> stands in for a concurrent
    /// writer whose own staging file is still open — a deterministic name means there is only ever ONE such
    /// path per cache file, so every process refreshing the shared default cache contends for it.
    /// <para>
    /// Against a deterministic staging name the save cannot even reach the rename: the
    /// <see cref="FileStream"/> opened <see cref="FileMode.Create"/> onto the held path fails first, the
    /// blanket catch swallows it, and with no cache to fall back on the caller silently receives an EMPTY
    /// model list. A per-write unique name removes the contention by construction.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetModelConfigs_SavesAndReturnsFreshData_WhileAnotherWritersStagingFileIsOpen()
    {
        var deterministicTemp = _cacheFile + ".tmp";

        // The other writer's in-flight staging file. FileShare.None is what a writer holding its own output
        // looks like to everyone else.
        await File.WriteAllTextAsync(deterministicTemp, "in-flight");
        using var _ = new FileStream(deterministicTemp, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var service = new OpenRouterModelService(_httpClient, NullLoggerOf(), _cacheFile);

        var result = await service.GetModelConfigsAsync();

        // The fetch must have reached the caller rather than degrading to the empty-list fallback.
        Assert.Equal(FreshModelSlug, Assert.Single(result).Id);

        // And it must have actually been persisted, which is the whole point of the save path.
        Assert.True(File.Exists(_cacheFile), "the refreshed cache must have landed on disk");
        Assert.Contains(FreshModelSlug, await File.ReadAllTextAsync(_cacheFile));
    }

    /// <summary>
    /// Pins the missing rename retry. A reader opened through <c>File.ReadAllTextAsync</c> holds
    /// <see cref="FileShare.Read"/>, which withholds delete access, and Windows <c>MoveFile</c> with
    /// <c>REPLACE_EXISTING</c> needs it — so a plain concurrent read of the cache is enough to make the rename
    /// throw <see cref="UnauthorizedAccessException"/>. A second process loading this shared cache while this
    /// one refreshes it is exactly that reader, and so is a virus scanner or the search indexer.
    /// <para>
    /// The cache seeded here is STALE, so the service fetches fresh data and must save it. Under the defect
    /// the rename throws on its first and only attempt, the blanket catch swallows it, and the caller silently
    /// receives the STALE model — which is why this asserts on the model's identity rather than on nothing
    /// having thrown.
    /// </para>
    /// <para>
    /// The handle is released partway through the retry budget, so this asserts the save WAITS OUT a transient
    /// holder rather than that it tolerates a permanent one. A fixed sleep in the service would not pass it
    /// either, because the release happens after the first attempt has already been made.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetModelConfigs_ReturnsFreshData_AfterWaitingOutATransientReaderHoldingTheCache()
    {
        await SeedStaleCacheAsync();

        // Exactly the sharing a concurrent File.ReadAllTextAsync of this file would take.
        var reader = new FileStream(_cacheFile, FileMode.Open, FileAccess.Read, FileShare.Read);

        var service = new OpenRouterModelService(_httpClient, NullLoggerOf(), _cacheFile);

        var released = false;
        var fetch = Task.Run(async () => await service.GetModelConfigsAsync());

        // Give the rename at least one attempt against the live handle before letting go, so a passing run
        // proves the retry ran rather than that the handle was gone before the service looked.
        await Task.Delay(60);
        released = true;
        await reader.DisposeAsync();

        var result = await fetch;

        Assert.True(released);
        Assert.Equal(FreshModelSlug, Assert.Single(result).Id);
        Assert.Contains(FreshModelSlug, await File.ReadAllTextAsync(_cacheFile));
    }

    /// <summary>
    /// Writes a cache older than the 24-hour validity window, so <c>GetModelConfigsAsync</c> fetches fresh
    /// data and reaches the save path instead of returning the cached models outright.
    /// </summary>
    private async Task SeedStaleCacheAsync()
    {
        var stale = new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-48),
            ModelsData = ModelsPayload(StaleModelSlug),
            ModelDetails = new Dictionary<string, JsonNode> { [StaleModelSlug] = DetailsPayload() },
        };

        var json = JsonSerializer.Serialize(
            stale,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = false }
        );

        await File.WriteAllTextAsync(_cacheFile, json);
    }

    private void SetupMockHttpResponses()
    {
        // A factory rather than a single instance: both endpoints are hit more than once across a run, and a
        // shared HttpResponseMessage's content stream can only be consumed once.
        Setup("/models", () => ModelsPayload(FreshModelSlug).ToJsonString());
        Setup("/stats/endpoint", () => DetailsPayload().ToJsonString());

        void Setup(string urlFragment, Func<string> body) =>
            _handler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains(urlFragment)),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(() =>
                    new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent(body()) }
                );
    }

    private static JsonNode ModelsPayload(string slug) =>
        JsonNode.Parse(
            $$"""
            { "data": [ { "slug": "{{slug}}", "name": "{{slug}}", "context_length": 4096 } ] }
            """
        )!;

    private static JsonNode DetailsPayload() =>
        JsonNode.Parse(
            """
            { "data": [ { "id": "test-endpoint", "provider_name": "TestProvider" } ] }
            """
        )!;

    private static ILogger<OpenRouterModelService> NullLoggerOf() => new Mock<ILogger<OpenRouterModelService>>().Object;
}
