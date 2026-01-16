namespace Domain.DTOs;

public record ChatResponseMessage
{
    public string? Message;
    public string? CalledTools;
    public string? Reasoning;
    public string? Error;
    public bool Bold;
    public bool IsComplete;
    public int MessageIndex;
}