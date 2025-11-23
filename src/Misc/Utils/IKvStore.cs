namespace AchieveAi.LmDotnetTools.Misc.Utils;

public interface IKvStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);

    Task<IAsyncEnumerable<string>> EnumerateKeysAsync(CancellationToken cancellationToken = default);
}
