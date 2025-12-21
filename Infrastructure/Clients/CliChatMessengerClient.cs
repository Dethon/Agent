using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Cli;

namespace Infrastructure.Clients;

public class CliChatMessengerClient : IChatMessengerClient, IDisposable
{
    private const int DefaultThreadId = 1;

    private readonly CliChatMessageRouter _router;
    private readonly ITerminalAdapter _terminalAdapter;

    public CliChatMessengerClient(
        string agentName,
        string userName,
        ITerminalAdapter terminalAdapter,
        Action? onShutdownRequested = null)
    {
        _terminalAdapter = terminalAdapter;
        _router = new CliChatMessageRouter(agentName, userName, terminalAdapter);

        if (onShutdownRequested is not null)
        {
            _router.ShutdownRequested += onShutdownRequested;
        }
    }

    public IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken)
    {
        return _router.ReadPrompts(cancellationToken).ToAsyncEnumerable();
    }

    public Task SendResponse(
        long chatId, ChatResponseMessage responseMessage, long? threadId, CancellationToken cancellationToken)
    {
        _router.SendResponse(responseMessage);
        return Task.CompletedTask;
    }

    public Task<int> CreateThread(long chatId, string name, CancellationToken cancellationToken)
    {
        _router.CreateThread(name);
        return Task.FromResult(DefaultThreadId);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    public void Dispose()
    {
        _router.Dispose();
        _terminalAdapter.Dispose();
        GC.SuppressFinalize(this);
    }
}