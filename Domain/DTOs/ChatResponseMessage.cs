namespace Domain.DTOs;

public record ChatResponseMessage
{
    public bool Bold;
    public string? CalledTools;
    public string? Error;
    public bool IsComplete;
    public string? Message;
    public int MessageIndex;
    public string? Reasoning;
}