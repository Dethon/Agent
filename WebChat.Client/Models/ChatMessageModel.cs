namespace WebChat.Client.Models;

public class ChatMessageModel
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string? Reasoning { get; set; }
    public string? ToolCalls { get; set; }
    public bool IsError { get; set; }
}