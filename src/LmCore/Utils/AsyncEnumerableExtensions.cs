namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
///     Extensions for working with asynchronous enumerables.
/// </summary>
public static class AsyncEnumerableExtensions
{
    public static IAsyncEnumerable<T> ToAsyncEnumerable<T>(this T[] array)
    {
        ArgumentNullException.ThrowIfNull(array);

        return array.ToAsyncEnumerableInternal();
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerableInternal<T>(this T[] array)
    {
        foreach (var item in array)
        {
            await Task.Yield(); // Add await to make this truly async
            yield return item;
        }
    }
}
