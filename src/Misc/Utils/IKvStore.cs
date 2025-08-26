namespace AchieveAi.LmDotnetTools.Misc.Utils;

public interface IKvStore
{
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);

    public Task<IAsyncEnumerable<string>> EnumerateKeysAsync(
        CancellationToken cancellationToken = default
    );
}
