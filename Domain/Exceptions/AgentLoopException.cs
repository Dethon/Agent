namespace Domain.Exceptions;

public class AgentLoopException : Exception
{
    public AgentLoopException() { }

    public AgentLoopException(string message) : base(message) { }

    public AgentLoopException(string message, Exception inner) : base(message, inner) { }
}