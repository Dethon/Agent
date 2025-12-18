using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Spectre.Console;

namespace Infrastructure.Clients;

public class CliChatMessengerClient(string agentName) : IChatMessengerClient
{
    private const long DefaultChatId = 1;
    private const int DefaultThreadId = 1;
    private int _messageCounter;

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new Rule($"[bold blue]{agentName}[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine("[dim]Type your message and press Enter. Type 'exit' to quit.[/]");
        AnsiConsole.WriteLine();

        while (!cancellationToken.IsCancellationRequested)
        {
            var prompt = AnsiConsole.Prompt(
                new TextPrompt<string>("[green]You:[/]")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(prompt))
            {
                continue;
            }

            if (prompt.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[yellow]Goodbye![/]");
                yield break;
            }

            yield return new ChatPrompt
            {
                Prompt = prompt,
                ChatId = DefaultChatId,
                MessageId = Interlocked.Increment(ref _messageCounter),
                Sender = Environment.UserName,
                ThreadId = DefaultThreadId
            };

            await Task.CompletedTask;
        }
    }

    public Task SendResponse(
        long chatId, ChatResponseMessage responseMessage, long? threadId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(responseMessage.CalledTools))
        {
            var toolPanel = new Panel(
                    new Text(responseMessage.CalledTools, new Style(Color.Grey)))
                .Header("[dim]Tools Called[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse("grey"))
                .Expand();
            AnsiConsole.Write(toolPanel);
        }

        if (!string.IsNullOrWhiteSpace(responseMessage.Message))
        {
            var style = responseMessage.Bold ? "bold cyan" : "cyan";
            AnsiConsole.MarkupLine($"[blue]{agentName}:[/] [{style}]{Markup.Escape(responseMessage.Message)}[/]");
        }

        AnsiConsole.WriteLine();
        return Task.CompletedTask;
    }

    public Task<int> CreateThread(long chatId, string name, CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new Rule($"[bold yellow]{Markup.Escape(name)}[/]").RuleStyle("yellow"));
        return Task.FromResult(DefaultThreadId);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }
}