using System.Runtime.CompilerServices;
using System.Text;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Clients.Messaging;

public sealed class OneShotChatMessengerClient(
    string prompt,
    bool showReasoning,
    IHostApplicationLifetime lifetime) : IChatMessengerClient
{
    private bool _promptSent;
    private bool _responseStarted;
    private readonly StringBuilder _responseBuilder = new();
    private readonly StringBuilder _reasoningBuilder = new();
    private readonly Lock _lock = new();

    public bool SupportsScheduledNotifications => false;

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_promptSent)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        _promptSent = true;
        yield return new ChatPrompt
        {
            Prompt = prompt,
            ChatId = 1,
            ThreadId = 1,
            MessageId = 1,
            Sender = Environment.UserName
        };
    }

    public async Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
        CancellationToken cancellationToken)
    {
        var responses = updates
            .Where(x => x.Item3 is not null)
            .Select(x => x.Item3!);

        await foreach (var response in responses.WithCancellation(cancellationToken))
        {
            if (response.IsComplete)
            {
                CompleteResponse();
                continue;
            }

            lock (_lock)
            {
                _responseStarted = true;

                if (!string.IsNullOrEmpty(response.Content))
                {
                    _responseBuilder.Append(response.Content);
                }

                if (showReasoning && !string.IsNullOrEmpty(response.Reasoning))
                {
                    _reasoningBuilder.Append(response.Reasoning);
                }

                FlushOutput();
            }
        }

        CompleteResponse();
    }

    public Task<int> CreateThread(long chatId, string name, string? agentId, CancellationToken cancellationToken)
    {
        return Task.FromResult(1);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    public Task<AgentKey> CreateTopicIfNeededAsync(
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
    {
        return Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId));
    }

    public Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    private void FlushOutput()
    {
        if (_reasoningBuilder.Length > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(_reasoningBuilder.ToString());
            Console.ResetColor();
            _reasoningBuilder.Clear();
        }

        if (_responseBuilder.Length <= 0)
        {
            return;
        }

        Console.Write(_responseBuilder.ToString());
        _responseBuilder.Clear();
    }

    private void CompleteResponse()
    {
        lock (_lock)
        {
            if (!_responseStarted)
            {
                return;
            }

            _responseStarted = false;
            FlushOutput();
            Console.WriteLine();
            lifetime.StopApplication();
        }
    }
}