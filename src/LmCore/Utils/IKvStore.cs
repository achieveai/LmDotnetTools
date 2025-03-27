using System.Collections.Generic;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

public interface IKvStore
{
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);

    public Task<IAsyncEnumerable<string>> EnumerateKeysAsync(CancellationToken cancellationToken = default);
}