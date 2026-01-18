using Microsoft.Extensions.Logging;

namespace Infrastructure.Extensions;

public static class TaskExtensions
{
    extension(Task task)
    {
        public async Task SafeAwaitAsync<T>(ILogger logger, string message, T arg)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException ex)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(ex, message, arg);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, message, arg);
            }
        }
    }
}