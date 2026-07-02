using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Domain.Extensions;

public static class ChatMessageExtensions
{
    private const string SenderIdKey = "SenderId";
    private const string TimestampKey = "Timestamp";
    private const string MemoryContextKey = "MemoryContext";
    private const string LocationKey = "Location";
    private const string SatelliteIdKey = "SatelliteId";
    private const string DismissedAlertKey = "DismissedAlert";
    private const string ConversationContextKey = "ConversationContext";

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

        public string? GetLocation()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(LocationKey);
            return value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null
            };
        }

        public void SetLocation(string? location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[LocationKey] = location;
        }

        public string? GetSatelliteId()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(SatelliteIdKey);
            return value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null
            };
        }

        public void SetSatelliteId(string? satelliteId)
        {
            if (string.IsNullOrWhiteSpace(satelliteId))
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[SatelliteIdKey] = satelliteId;
        }

        public string? GetDismissedAlert()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(DismissedAlertKey);
            return value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null
            };
        }

        public void SetDismissedAlert(string? dismissedAlert)
        {
            if (string.IsNullOrWhiteSpace(dismissedAlert))
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[DismissedAlertKey] = dismissedAlert;
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
            var value = message.AdditionalProperties?.GetValueOrDefault(MemoryContextKey);
            return value switch
            {
                MemoryContext context => context,
                JsonElement je => je.Deserialize<MemoryContext>(),
                _ => null
            };
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

        public ConversationContext? GetConversationContext()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(ConversationContextKey);
            return value switch
            {
                ConversationContext context => context,
                JsonElement je => je.Deserialize<ConversationContext>(ChannelProtocol.SerializerOptions),
                _ => null
            };
        }

        public void SetConversationContext(ConversationContext? context)
        {
            if (context is null)
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[ConversationContextKey] = context;
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