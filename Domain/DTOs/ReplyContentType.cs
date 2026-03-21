using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public static class ReplyContentType
{
    public const string Text = "text";
    public const string Reasoning = "reasoning";
    public const string ToolCall = "tool_call";
    public const string Error = "error";
    public const string StreamComplete = "stream_complete";
}
