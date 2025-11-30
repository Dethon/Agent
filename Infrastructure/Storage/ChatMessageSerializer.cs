using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Infrastructure.Storage;

public static class ChatMessageSerializer
{
    public static byte[] Serialize(IEnumerable<ChatMessage> messages)
    {
        return JsonSerializer.SerializeToUtf8Bytes(messages.ToArray());
    }

    public static ChatMessage[] Deserialize(byte[] data)
    {
        return JsonSerializer.Deserialize<ChatMessage[]>(data) ?? [];
    }
}