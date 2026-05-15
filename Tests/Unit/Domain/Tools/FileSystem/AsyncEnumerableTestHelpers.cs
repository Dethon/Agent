namespace Tests.Unit.Domain.Tools.FileSystem;

internal static class AsyncEnumerableTestHelpers
{
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ToAsyncEnumerable(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }
}