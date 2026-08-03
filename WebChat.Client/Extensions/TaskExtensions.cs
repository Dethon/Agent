namespace WebChat.Client.Extensions;

public static class TaskExtensions
{
    extension(Task task)
    {
        // Effects start work and abandon the task by construction, so a throw inside it is
        // invisible unless something observes the fault. This is that something: it returns
        // nothing, because there is no caller left to hand a task back to.
        public void LogFaults(ILogger logger, string? context = null)
        {
            ArgumentNullException.ThrowIfNull(logger);

            task.ContinueWith(
                faulted => logger.LogError(
                    faulted.Exception?.GetBaseException(),
                    "Abandoned effect task failed ({Context})",
                    context ?? "no context"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}