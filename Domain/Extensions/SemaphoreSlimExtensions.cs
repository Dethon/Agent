namespace Domain.Extensions;

public static class SemaphoreSlimExtensions
{
    extension(SemaphoreSlim semaphore)
    {
        public async Task<TResult> WaitAsync<TResult>(
            Func<CancellationToken, Task<TResult>> func, CancellationToken cancellationToken = default)
        {
            try
            {
                await semaphore.WaitAsync(cancellationToken);
                return await func(cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task WaitAsync(Func<CancellationToken, Task> func, CancellationToken cancellationToken = default)
        {
            try
            {
                await semaphore.WaitAsync(cancellationToken);
                await func(cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task<TResult> WaitAsync<TResult>(Func<TResult> func, CancellationToken cancellationToken = default)
        {
            try
            {
                await semaphore.WaitAsync(cancellationToken);
                return func();
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task WaitAsync(Action func, CancellationToken cancellationToken = default)
        {
            try
            {
                await semaphore.WaitAsync(cancellationToken);
                func();
            }
            finally
            {
                semaphore.Release();
            }
        }
    }
}