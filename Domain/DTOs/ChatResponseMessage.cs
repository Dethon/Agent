namespace Domain.DTOs;

public record ChatResponseMessage
{
    public string? Message;
    public string? CalledTools;
    public string? Reasoning;
    public bool Bold;
}