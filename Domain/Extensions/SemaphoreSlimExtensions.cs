namespace Domain.Extensions;

public static class SemaphoreSlimExtensions
{
    extension(SemaphoreSlim semaphore)
    {
        public async Task<T> WithLockAsync<T>(Func<Task<T>> action,
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

        public async Task WithLockAsync(Func<Task> action,
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
}