using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Domain.Extensions;

public static class ChatMessageExtensions
{
    private const string SenderIdKey = "SenderId";

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
    }
}