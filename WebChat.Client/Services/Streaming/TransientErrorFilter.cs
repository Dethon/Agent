namespace WebChat.Client.Services.Streaming;

public static class TransientErrorFilter
{
    private static readonly string[] TransientPatterns =
    [
        "OperationCanceled",
        "TaskCanceled",
        "operation was canceled"
    ];

    public static bool IsTransientException(Exception ex)
    {
        return ex is OperationCanceledException or TaskCanceledException;
    }

    public static bool IsTransientErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return true;

        return TransientPatterns.Any(pattern =>
            message.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}
