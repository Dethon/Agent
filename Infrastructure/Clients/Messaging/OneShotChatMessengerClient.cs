using System.Runtime.CompilerServices;
using System.Text;
using Domain.Contracts;
using Domain.DTOs;
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
    private Timer? _completionTimer;
    private readonly Lock _lock = new();

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

    public Task SendResponse(
        long chatId, ChatResponseMessage responseMessage, long? threadId, string? botTokenHash,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _responseStarted = true;
            _completionTimer?.Dispose();
            _completionTimer = null;

            if (!string.IsNullOrEmpty(responseMessage.Message))
            {
                _responseBuilder.Append(responseMessage.Message);
            }

            if (showReasoning && !string.IsNullOrEmpty(responseMessage.Reasoning))
            {
                _reasoningBuilder.Append(responseMessage.Reasoning);
            }

            FlushOutput();

            // Start/reset completion timer after each response chunk
            _completionTimer = new Timer(_ => CompleteResponse(), null, 500, Timeout.Infinite);
        }

        return Task.CompletedTask;
    }

    public Task<int> CreateThread(long chatId, string name, string? botTokenHash, CancellationToken cancellationToken)
    {
        return Task.FromResult(1);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, string? botTokenHash,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
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

            FlushOutput();
            Console.WriteLine();
            lifetime.StopApplication();
        }
    }
}