namespace Domain.Exceptions;

public class HomeAssistantException : Exception
{
    public int? StatusCode { get; }

    public HomeAssistantException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

public sealed class HomeAssistantUnauthorizedException(string message)
    : HomeAssistantException(message, 401);

public sealed class HomeAssistantNotFoundException(string message)
    : HomeAssistantException(message, 404);