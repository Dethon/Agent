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
            AnsiConsole.Write(new Rule
            {
                Style = "grey"
            });

            await _askSemaphore.WaitAsync(cancellationToken);
            var input = await AnsiConsole.AskAsync<string>("[blue]You:[/]", cancellationToken);
            _busySemaphore.Release();

            AnsiConsole.Write(new Rule
            {
                Style = "grey dim"
            });

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

    public Task SendResponse(long chatId, string response, long? threadId, CancellationToken cancellationToken)
    {
        if (chatId != CliChatId || threadId != _threadId)
        {
            return Task.CompletedTask;
        }

        AnsiConsole.Markup("[green]ChatGPT:[/]\n");
        Console.WriteLine(response.Trim());

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

    public async Task BlockWhile(long chatId, long? threadId, Func<Task> task, CancellationToken cancellationToken)
    {
        await _busySemaphore.WaitAsync(cancellationToken);
        await AnsiConsole.Status().StartAsync($"{agentName} is thinking...", _ => task());
        _askSemaphore.Release();
    }
}