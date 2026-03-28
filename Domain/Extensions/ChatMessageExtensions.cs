using System.Text.Json;
using Domain.DTOs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Domain.Extensions;

public static class ChatMessageExtensions
{
    private const string SenderIdKey = "SenderId";
    private const string TimestampKey = "Timestamp";
    private const string MemoryContextKey = "MemoryContext";

    extension(ChatMessage message)
    {
        public string? GetSenderId()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(SenderIdKey);
            return value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null
            };
        }

        public void SetSenderId(string? senderId)
        {
            if (senderId is null)
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[SenderIdKey] = senderId;
        }

        public DateTimeOffset? GetTimestamp()
        {
            return ParseTimestamp(message.AdditionalProperties?.GetValueOrDefault(TimestampKey));
        }

        public void SetTimestamp(DateTimeOffset? timestamp)
        {
            if (timestamp is null)
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[TimestampKey] = timestamp.Value;
        }

        public MemoryContext? GetMemoryContext()
        {
            return message.AdditionalProperties?.GetValueOrDefault(MemoryContextKey) as MemoryContext;
        }

        public void SetMemoryContext(MemoryContext? context)
        {
            if (context is null)
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[MemoryContextKey] = context;
        }
    }

    extension(ChatResponseUpdate update)
    {
        public void SetTimestamp(DateTimeOffset timestamp)
        {
            update.AdditionalProperties ??= [];
            update.AdditionalProperties[TimestampKey] = timestamp;
        }
    }

    extension(AgentResponseUpdate update)
    {
        public DateTimeOffset? GetTimestamp()
        {
            return ParseTimestamp(update.AdditionalProperties?.GetValueOrDefault(TimestampKey));
        }
    }

    private static DateTimeOffset? ParseTimestamp(object? value)
    {
        return value switch
        {
            DateTimeOffset dto => dto,
            string s when DateTimeOffset.TryParse(s, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } je
                when DateTimeOffset.TryParse(je.GetString(), out var parsed) => parsed,
            _ => null
        };
    }
}