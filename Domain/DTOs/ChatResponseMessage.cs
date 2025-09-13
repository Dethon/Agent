namespace Domain.DTOs;

public record ChatResponseMessage
{
    public string? Message;
    public string? CalledTools;
    public bool Bold;
}