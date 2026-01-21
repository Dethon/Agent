using Domain.DTOs.WebChat;

namespace Infrastructure.Clients.Messaging;

internal sealed class StreamBuffer
{
    private const int MaxBufferSize = 100;
    private readonly Lock _lock = new();
    private readonly List<ChatStreamMessage> _messages = [];

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