using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Spectre.Console;

namespace Infrastructure.Clients;

public class CliChatMessengerClient(string agentName) : IChatMessengerClient
{
    private const string UserName = "You";
    private const long CliChatId = 1;
    private int _threadId = 1;
    private int _messageId = 1;

    private readonly SemaphoreSlim _askSemaphore = new(1, 1);
    private readonly SemaphoreSlim _busySemaphore = new(0, 1);

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new FigletText(agentName).Centered().Color(Color.Yellow));
        AnsiConsole.Write(new Text("Ctrl + C to quit.\n", new Style(Color.Grey)).Centered());
        while (!cancellationToken.IsCancellationRequested)
        {
            await _askSemaphore.WaitAsync(cancellationToken);
            var input = await AnsiConsole.AskAsync<string>("\n[blue]You:[/]", cancellationToken);
            AnsiConsole.WriteLine();
            _busySemaphore.Release();

            yield return new ChatPrompt
            {
                Prompt = input,
                ChatId = CliChatId,
                MessageId = Interlocked.Increment(ref _messageId),
                ThreadId = _threadId,
                Sender = UserName
            };
        }
    }

    public Task SendResponse(long chatId, ChatResponseMessage responseMessage, long? threadId,
        CancellationToken cancellationToken)
    {
        if (chatId != CliChatId || threadId != _threadId)
        {
            return Task.CompletedTask;
        }

        AnsiConsole.MarkupLine($"[green]{agentName}:[/]");
        if (!string.IsNullOrEmpty(responseMessage.Message))
        {
            if (responseMessage.Bold)
            {
                AnsiConsole.MarkupLineInterpolated($"[bold]{responseMessage.Message}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine(responseMessage.Message);
            }
        }

        if (!string.IsNullOrEmpty(responseMessage.CalledTools))
        {
            AnsiConsole.MarkupLineInterpolated($"[italic grey]{responseMessage.CalledTools}[/]");
        }


        return Task.CompletedTask;
    }

    public Task<int> CreateThread(long chatId, string name, CancellationToken cancellationToken)
    {
        return Task.FromResult(Interlocked.Increment(ref _threadId));
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, CancellationToken cancellationToken)
    {
        return Task.FromResult(threadId == _threadId);
    }

    public async Task BlockWhile(long chatId, long? threadId, Func<CancellationToken, Task> task, CancellationToken ct)
    {
        await _busySemaphore.WaitAsync(ct);
        await AnsiConsole.Status().StartAsync($"{agentName} is thinking...", _ => task(ct));
        _askSemaphore.Release();
    }
}