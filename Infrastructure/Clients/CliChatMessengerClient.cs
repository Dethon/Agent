using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Infrastructure.Clients;

public class CliChatMessengerClient : IChatMessengerClient
{
    private const long CliChatId = 1;
    private static int _nextMessageId;
    private static int _nextThreadId = 1;

    private readonly ConcurrentDictionary<int, string> _threads = new();
    private readonly ConcurrentQueue<string> _pendingAgentMessages = new();
    private readonly List<string> _messages = new();
    private readonly object _messagesLock = new();

    private int? _activeThreadId;
    private volatile bool _chatEnabled = true; // global enable/disable flag
    private const int MaxMessages = 300;

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Show initial banner (outside live region)
        AnsiConsole.MarkupLine(
            "[bold yellow]CLI Chat started. Use /new for new thread. /exit to stop input. Ctrl+C to terminate process.[/]");

        var channel = Channel.CreateUnbounded<ChatPrompt>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // Start interactive UI loop in background
        var loopTask = Task.Run(() => RunInteractiveLoopAsync(channel.Writer, cancellationToken), cancellationToken);

        while (await channel.Reader.WaitToReadAsync(cancellationToken))
        {
            while (channel.Reader.TryRead(out var prompt))
            {
                yield return prompt;
            }
        }

        try { await loopTask; }
        catch (OperationCanceledException)
        {
            /* ignore */
        }
    }

    public Task<int> SendResponse(long chatId, string response, long? messageThreadId,
        CancellationToken cancellationToken)
    {
        var messageId = Interlocked.Increment(ref _nextMessageId);
        var threadInfo = messageThreadId is null ? "new" : messageThreadId.ToString();
        var clean = ConvertToMarkupWithCodeBlocks(response);
        _pendingAgentMessages.Enqueue($"[bold cyan]Agent[/][grey](thread {threadInfo})[/]: {clean}");
        return Task.FromResult(messageId);
    }

    public Task<int> CreateThread(long chatId, string name, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextThreadId);
        _threads[id] = name;
        if (_activeThreadId is null)
        {
            _activeThreadId = id;
        }

        _pendingAgentMessages.Enqueue($"[bold magenta]Created thread {id}[/]: {Markup.Escape(name)}");
        return Task.FromResult(id);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_threads.ContainsKey((int)threadId));
    }

    public Task DisableChat(long chatId, long? messageThreadId, CancellationToken cancellationToken)
    {
        _chatEnabled = false;
        var scope = messageThreadId is null ? "all threads" : $"thread {messageThreadId}";
        _pendingAgentMessages.Enqueue($"[bold red]Chat disabled for {scope}. Input locked.[/]");
        return Task.CompletedTask;
    }

    public Task EnableChat(long chatId, long? messageThreadId, CancellationToken cancellationToken)
    {
        _chatEnabled = true;
        var scope = messageThreadId is null ? "all threads" : $"thread {messageThreadId}";
        _pendingAgentMessages.Enqueue($"[bold green]Chat re-enabled for {scope}. You may type now.[/]");
        return Task.CompletedTask;
    }

    // ================= UI / Live Loop =================

    private async Task RunInteractiveLoopAsync(ChannelWriter<ChatPrompt> writer, CancellationToken ct)
    {
        try
        {
            await AnsiConsole.Live(BuildLayout("Ready"))
                .AutoClear(false)
                .StartAsync(async ctx =>
                {
                    var spinner = Spinner.Known.Dots2;
                    var spinnerFrames = spinner.Frames.ToArray();
                    var spinnerIndex = 0;

                    while (!ct.IsCancellationRequested)
                    {
                        // Drain pending agent messages
                        while (_pendingAgentMessages.TryDequeue(out var agentLine))
                        {
                            AppendMessage(agentLine);
                        }

                        if (!_chatEnabled)
                        {
                            // Disabled state spinner update
                            var frame = spinnerFrames[spinnerIndex];
                            spinnerIndex = (spinnerIndex + 1) % spinnerFrames.Length;
                            ctx.UpdateTarget(BuildLayout($"[yellow]{frame} Chat disabled...[/]"));
                            ctx.Refresh();
                            await Task.Delay(120, ct);
                            continue;
                        }

                        // Enabled state: we cannot safely use AnsiConsole.Prompt inside Live (causes exclusivity conflict)
                        // So we end the live refresh cycle momentarily, perform a raw Console.ReadLine, then resume.
                        var threadLabel = _activeThreadId?.ToString() ?? "new";
                        ctx.UpdateTarget(BuildLayout($"[green]Chat enabled[/] (thread: [grey]{threadLabel}[/])"));
                        ctx.Refresh();

                        // Display simple prompt prefix (Spectre markup allowed) outside of managed live area
                        AnsiConsole.Markup("[bold green]You>[/] ");
                        string? input = null;
                        try
                        {
                            // Blocking read from console; no Spectre interactive prompt to avoid concurrency issue
                            input = Console.ReadLine();
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        if (ct.IsCancellationRequested)
                            break;

                        if (input is null)
                            continue;

                        input = input.TrimEnd();
                        if (string.IsNullOrWhiteSpace(input))
                            continue;

                        if (string.Equals(input, "/exit", StringComparison.OrdinalIgnoreCase))
                        {
                            AppendMessage("[bold yellow]Input session ended by user (/exit).[/]");
                            break;
                        }

                        if (string.Equals(input, "/new", StringComparison.OrdinalIgnoreCase))
                        {
                            _activeThreadId = null;
                            AppendMessage("[grey]Thread context cleared. Next user message creates a new thread.[/]");
                            continue;
                        }

                        var isCommand = input.StartsWith('/');
                        var promptText = isCommand ? input.TrimStart('/') : input;
                        var messageId = Interlocked.Increment(ref _nextMessageId);
                        var prompt = new ChatPrompt
                        {
                            Prompt = promptText,
                            ChatId = CliChatId,
                            MessageId = messageId,
                            ThreadId = _activeThreadId,
                            Sender = Environment.UserName,
                            IsCommand = isCommand,
                            ReplyToMessageId = null
                        };

                        AppendMessage(
                            $"[bold green]You[/]{(_activeThreadId is null ? "[grey](new)[/]" : $"[grey](thread {_activeThreadId})[/]")}: {Markup.Escape(input)}");

                        await writer.WriteAsync(prompt, ct);

                        // Drain agent messages right after user input for snappier UI
                        while (_pendingAgentMessages.TryDequeue(out var postLine))
                        {
                            AppendMessage(postLine);
                        }

                        ctx.UpdateTarget(BuildLayout($"[green]Chat enabled[/] (thread: [grey]{threadLabel}[/])"));
                        ctx.Refresh();
                    }
                });
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private void AppendMessage(string markupLine)
    {
        lock (_messagesLock)
        {
            _messages.Add(markupLine);
            if (_messages.Count > MaxMessages)
            {
                _messages.RemoveRange(0, _messages.Count - MaxMessages);
            }
        }
    }

    private IRenderable BuildLayout(string stateMessage)
    {
        var transcriptRenderable = BuildTranscriptRenderable();

        var messagesPanel = new Panel(transcriptRenderable)
        {
            Header = new PanelHeader(" Chat ", Justify.Left),
            Border = BoxBorder.Rounded,
            Expand = true
        };

        var statusTable = new Table().AddColumn(new TableColumn("Status").Centered());
        statusTable.Border = TableBorder.None;
        statusTable.AddRow(new Markup(stateMessage));
        statusTable.AddRow(new Markup(_chatEnabled ? "[green]Input: ENABLED[/]" : "[red]Input: DISABLED[/]"));
        if (_activeThreadId is not null)
        {
            statusTable.AddRow(new Markup($"Thread: [grey]{_activeThreadId}[/]"));
        }

        var statusPanel = new Panel(statusTable)
        {
            Border = BoxBorder.Square,
            Header = new PanelHeader(" Status ")
        };

        return new Rows(messagesPanel, statusPanel);
    }

    private IRenderable BuildTranscriptRenderable()
    {
        List<string> snapshot;
        lock (_messagesLock)
        {
            snapshot = _messages.ToList();
        }

        if (snapshot.Count == 0)
        {
            return new Markup("[grey]No messages yet[/]");
        }

        var table = new Table
        {
            Border = TableBorder.None,
            Expand = true,
            ShowRowSeparators = false
        };
        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        foreach (var line in snapshot)
        {
            IRenderable renderable;
            try
            {
                // Validate markup; if invalid fall back to escaped
                renderable = new Markup(line);
            }
            catch
            {
                renderable = new Markup(Markup.Escape(line));
            }

            table.AddRow(renderable);
        }

        return table;
    }

    // ================= Formatting & Parsing =================

    private static string ConvertToMarkupWithCodeBlocks(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // Normalize newlines
        var text = input.Replace("\r\n", "\n").Replace('\r', '\n');

        // Extract and handle <pre><code> blocks first
        text = Regex.Replace(text, "<pre><code>([\\s\\S]*?)</code></pre>", m => FormatCodeBlock(m.Groups[1].Value));
        text = Regex.Replace(text, "<code>([\\s\\S]*?)</code>", m => FormatInlineCode(m.Groups[1].Value));

        // Handle fenced code blocks ```lang?\n...\n```
        text = Regex.Replace(text, "```(\\w+)?\\n([\\s\\S]*?)```", m =>
        {
            var lang = m.Groups[1].Value;
            var code = m.Groups[2].Value.TrimEnd();
            return FormatCodeBlock(code, lang);
        });

        // Basic HTML tag replacements for bold/italic/linebreak
        text = text.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n")
            .Replace("<b>", "[bold]").Replace("</b>", "[/]")
            .Replace("<strong>", "[bold]").Replace("</strong>", "[/]")
            .Replace("<i>", "[italic]").Replace("</i>", "[/]")
            .Replace("<em>", "[italic]").Replace("</em>", "[/]");

        // Strip remaining HTML tags
        text = Regex.Replace(text, "<[^>]+>", string.Empty);

        // Escape markup control chars, then restore inserted tags
        text = text.Replace("[", "[[").Replace("]", "]]");
        text = RestoreTag(text, "bold");
        text = RestoreTag(text, "italic");

        return text;

        static string RestoreTag(string txt, string tag)
        {
            return txt.Replace($"[[{tag}]", $"[{tag}]").Replace("[[/]]", "[/]");
        }

        static string FormatInlineCode(string code)
        {
            return $"[grey]{Markup.Escape(code)}[/]";
        }

        static string FormatCodeBlock(string code, string? lang = null)
        {
            var header = string.IsNullOrWhiteSpace(lang) ? "code" : lang.ToLowerInvariant();
            var escaped = Markup.Escape(code);
            // Use a panel-like visual with rule lines
            var sb = new StringBuilder();
            sb.AppendLine($"[dim]┌─ {header} ────────────────────────────[/]");
            foreach (var line in escaped.Split('\n'))
            {
                sb.AppendLine("[dim]│[/] " + (string.IsNullOrEmpty(line) ? " " : line));
            }

            sb.Append("[dim]└───────────────────────────────────────[/]");
            return sb.ToString();
        }
    }
}