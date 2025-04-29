namespace Domain.Exceptions;

public class MissingDownloadException : Exception
{
    public MissingDownloadException() { }

    public MissingDownloadException(string message) : base(message) { }

    public MissingDownloadException(string message, Exception inner) : base(message, inner) { }
}