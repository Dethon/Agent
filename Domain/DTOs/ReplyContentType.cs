using System.Text.Json.Serialization;
using Domain.Json;

namespace Domain.DTOs;

[JsonConverter(typeof(SnakeCaseLowerEnumConverter<ReplyContentType>))]
public enum ReplyContentType
{
    Text,
    Reasoning,
    ToolCall,
    Error,
    StreamComplete
}
