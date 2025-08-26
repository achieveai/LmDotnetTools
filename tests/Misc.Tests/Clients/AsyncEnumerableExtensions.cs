namespace AchieveAi.LmDotnetTools.Misc.Tests.Clients;

/// <summary>
/// Extension methods for testing async enumerables.
/// </summary>
internal static class AsyncEnumerableExtensions
{
    /// <summary>
    /// Converts an array to an async enumerable for testing purposes.
    /// </summary>
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this T[] items)
    {
        foreach (var item in items)
        {
            await Task.Delay(5); // Small delay to simulate async behavior
            yield return item;
        }
    }
}
