using Domain.DTOs.WebChat;

namespace Infrastructure.Clients.Messaging;

internal sealed class StreamBuffer
{
    private readonly List<ChatStreamMessage> _messages = [];
    private readonly Lock _lock = new();
    private const int MaxBufferSize = 100;

    public void Add(ChatStreamMessage message)
    {
        lock (_lock)
        {
            if (_messages.Count >= MaxBufferSize)
            {
                _messages.RemoveAt(0);
            }

            _messages.Add(message);
        }
    }

    public IReadOnlyList<ChatStreamMessage> GetAll()
    {
        lock (_lock)
        {
            return [.. _messages];
        }
    }
}