using System.Runtime.CompilerServices;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Utilities;

/// <summary>
/// Extension methods for working with IAsyncEnumerable.
/// </summary>
public static class AsyncEnumerableExtensions
{
    /// <summary>
    /// Converts a collection to an IAsyncEnumerable.
    /// </summary>
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        this IEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Add an await operation to make this method truly async
            await Task.Yield();
            yield return item;
        }
    }

    /// <summary>
    /// Converts an IAsyncEnumerable to a List.
    /// </summary>
    public static async Task<List<T>> ToListAsync<T>(
        this IAsyncEnumerable<T> source,
        CancellationToken cancellationToken = default
    )
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            list.Add(item);
        }
        return list;
    }
}
