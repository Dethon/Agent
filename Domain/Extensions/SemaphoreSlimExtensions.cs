namespace Domain.Extensions;

public static class SemaphoreSlimExtensions
{
    public static async Task<T> WithLockAsync<T>(
        this SemaphoreSlim semaphore,
        Func<Task<T>> action,
        CancellationToken ct = default)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            semaphore.Release();
        }
    }

    public static async Task WithLockAsync(
        this SemaphoreSlim semaphore,
        Func<Task> action,
        CancellationToken ct = default)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            semaphore.Release();
        }
    }
}