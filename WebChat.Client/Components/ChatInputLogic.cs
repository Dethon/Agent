namespace WebChat.Client.Components;

public static class ChatInputLogic
{
    public static bool CanSend(bool disabled, string? inputText, bool isStreaming) =>
        !disabled && !isStreaming && !string.IsNullOrWhiteSpace(inputText);
}