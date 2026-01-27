using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Fixtures;

public sealed class FakeChatMessagingService : IChatMessagingService
{
    private readonly Queue<ChatStreamMessage> _enqueuedMessages = new();
    private readonly Dictionary<string, StreamState> _streamStates = new();
    private readonly HashSet<string> _cancelledTopics = new();
    private bool _enqueueResult = true;
    private bool _blockUntilComplete;
    private readonly TaskCompletionSource _completionSource = new();
    private Exception? _exceptionToThrow;

    public void SetExceptionToThrow(Exception? exception) => _exceptionToThrow = exception;

    public void SetEnqueueResult(bool result) => _enqueueResult = result;

    public void SetBlockUntilComplete(bool block)
    {
        _blockUntilComplete = block;
    }

    public void UnblockCompletion()
    {
        _completionSource.TrySetResult();
    }

    public int StreamDelayMs { get; set; } = 0;

    public void EnqueueMessages(params ChatStreamMessage[] messages)
    {
        foreach (var msg in messages)
        {
            _enqueuedMessages.Enqueue(msg);
        }
    }

    public void EnqueueContent(params string[] contents)
    {
        var messageId = Guid.NewGuid().ToString();
        foreach (var content in contents)
        {
            _enqueuedMessages.Enqueue(new ChatStreamMessage { Content = content, MessageId = messageId });
        }

        _enqueuedMessages.Enqueue(new ChatStreamMessage { IsComplete = true, MessageId = messageId });
    }

    public void EnqueueReasoning(params string[] reasonings)
    {
        var messageId = Guid.NewGuid().ToString();
        foreach (var reasoning in reasonings)
        {
            _enqueuedMessages.Enqueue(new ChatStreamMessage { Reasoning = reasoning, MessageId = messageId });
        }
    }

    public void EnqueueError(string error)
    {
        _enqueuedMessages.Enqueue(new ChatStreamMessage { Error = error, IsComplete = true });
    }

    public void SetStreamState(string topicId, StreamState state)
    {
        _streamStates[topicId] = state;
    }

    public void ClearStreamState(string topicId)
    {
        _streamStates.Remove(topicId);
    }

    public IReadOnlySet<string> CancelledTopics => _cancelledTopics;

    public async IAsyncEnumerable<ChatStreamMessage> SendMessageAsync(string topicId, string message,
        string? correlationId = null)
    {
        if (_exceptionToThrow is not null)
        {
            throw _exceptionToThrow;
        }

        if (_blockUntilComplete)
        {
            await _completionSource.Task;
        }

        while (_enqueuedMessages.TryDequeue(out var msg))
        {
            if (StreamDelayMs > 0)
            {
                await Task.Delay(StreamDelayMs);
            }

            yield return msg;
        }
    }

    public async IAsyncEnumerable<ChatStreamMessage> ResumeStreamAsync(string topicId)
    {
        if (_exceptionToThrow is not null)
        {
            throw _exceptionToThrow;
        }

        while (_enqueuedMessages.TryDequeue(out var msg))
        {
            if (StreamDelayMs > 0)
            {
                await Task.Delay(StreamDelayMs);
            }

            yield return msg;
        }
    }

    public Task<StreamState?> GetStreamStateAsync(string topicId)
    {
        return Task.FromResult(
            _streamStates.TryGetValue(topicId, out var state) ? state : null);
    }

    public Task CancelTopicAsync(string topicId)
    {
        _cancelledTopics.Add(topicId);
        return Task.CompletedTask;
    }

    public Task<bool> EnqueueMessageAsync(string topicId, string message, string? correlationId = null)
    {
        return Task.FromResult(_enqueueResult);
    }
}