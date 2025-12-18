using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace Infrastructure.Clients;

public class CliChatMessengerClient : IChatMessengerClient, IDisposable
{
    private const long DefaultChatId = 1;
    private const int DefaultThreadId = 1;

    private readonly string _agentName;
    private BlockingCollection<string> _inputQueue = new();
    private readonly List<ChatMessage> _chatHistory = [];
    private readonly List<ChatLine> _displayLines = [];
    private readonly object _historyLock = new();

    private ListView? _chatListView;
    private TextField? _inputField;
    private Window? _mainWindow;
    private int _messageCounter;
    private bool _isRunning;
    private bool _headerDisplayed;

    public CliChatMessengerClient(string agentName)
    {
        _agentName = agentName;
    }

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_headerDisplayed)
        {
            StartTerminalGui();
            _headerDisplayed = true;
        }

        while (!cancellationToken.IsCancellationRequested && _isRunning)
        {
            string? input = null;
            try
            {
                if (_inputQueue.IsCompleted)
                {
                    yield break;
                }

                input = _inputQueue.Take(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                // Collection was marked as complete
                yield break;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            AddToHistory(Environment.UserName, input, isUser: true);

            yield return new ChatPrompt
            {
                Prompt = input,
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
            AddToHistory("[Tools]", responseMessage.CalledTools, isUser: false, isToolCall: true);
        }

        if (!string.IsNullOrWhiteSpace(responseMessage.Message))
        {
            AddToHistory(_agentName, responseMessage.Message, isUser: false);
        }

        return Task.CompletedTask;
    }

    public Task<int> CreateThread(long chatId, string name, CancellationToken cancellationToken)
    {
        AddToHistory("[System]", $"--- {name} ---", isUser: false, isSystem: true);
        return Task.FromResult(DefaultThreadId);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    private void StartTerminalGui()
    {
        _isRunning = true;

        var guiThread = new Thread(() =>
        {
            Application.Init();

            var topColor = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.White, Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.White, Color.Black),
                HotNormal = Application.Driver.MakeAttribute(Color.Cyan, Color.Black),
                HotFocus = Application.Driver.MakeAttribute(Color.Cyan, Color.Black)
            };

            _mainWindow = new Window($" {_agentName} - Chat ")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ColorScheme = topColor
            };

            var helpLabel = new Label("Commands: /help | /clear    [Scroll: Up/Down/PageUp/PageDown]")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                ColorScheme = new ColorScheme
                {
                    Normal = Application.Driver.MakeAttribute(Color.BrightYellow, Color.DarkGray)
                }
            };

            var chatFrame = new FrameView("Chat History")
            {
                X = 0,
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 4,
                ColorScheme = new ColorScheme
                {
                    Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                    Focus = Application.Driver.MakeAttribute(Color.Gray, Color.Black)
                }
            };

            _chatListView = new ListView(new ChatListDataSource(_displayLines))
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                AllowsMarking = false,
                CanFocus = true,
                ColorScheme = new ColorScheme
                {
                    Normal = Application.Driver.MakeAttribute(Color.White, Color.Black),
                    Focus = Application.Driver.MakeAttribute(Color.White, Color.Black)
                }
            };

            chatFrame.Add(_chatListView);

            var inputFrame = new FrameView("Your Message")
            {
                X = 0,
                Y = Pos.Bottom(chatFrame),
                Width = Dim.Fill(),
                Height = 3,
                ColorScheme = new ColorScheme
                {
                    Normal = Application.Driver.MakeAttribute(Color.Green, Color.Black),
                    Focus = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black)
                }
            };

            _inputField = new TextField("")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                ColorScheme = new ColorScheme
                {
                    Normal = Application.Driver.MakeAttribute(Color.White, Color.Black),
                    Focus = Application.Driver.MakeAttribute(Color.White, Color.Black)
                }
            };

            _inputField.KeyPress += args =>
            {
                if (args.KeyEvent.Key == Key.Enter)
                {
                    var input = _inputField.Text?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(input))
                    {
                        ProcessInput(input);
                        _inputField.Text = "";
                    }

                    args.Handled = true;
                }
            };

            inputFrame.Add(_inputField);

            _mainWindow.Add(helpLabel, chatFrame, inputFrame);

            Application.Top.Add(_mainWindow);
            _inputField.SetFocus();

            AddToHistory("[System]", $"Welcome to {_agentName}!", isUser: false, isSystem: true);
            AddToHistory("[System]", "Type your message below and press Enter to send.", isUser: false, isSystem: true);

            Application.Run();
            Application.Shutdown();
        })
        {
            IsBackground = true
        };

        guiThread.Start();

        Thread.Sleep(500);
    }

    private void ProcessInput(string input)
    {
        switch (input.ToLowerInvariant())
        {
            case "/clear":
            case "/cls":
                ClearChatHistory();
                break;

            case "/help":
            case "/?":
                AddToHistory("[Help]", "Available commands:", isUser: false, isSystem: true);
                AddToHistory("[Help]", "  /help, /?     - Show this help", isUser: false, isSystem: true);
                AddToHistory("[Help]", "  /clear, /cls  - Clear conversation and start fresh", isUser: false,
                    isSystem: true);
                break;

            default:
                _inputQueue.Add(input);
                break;
        }
    }

    private void AddToHistory(string sender, string message, bool isUser, bool isToolCall = false,
        bool isSystem = false)
    {
        lock (_historyLock)
        {
            var chatMessage = new ChatMessage(sender, message, isUser, isToolCall, isSystem, DateTime.Now);
            _chatHistory.Add(chatMessage);
            AddDisplayLines(chatMessage);
        }

        UpdateChatView();
    }

    private void ClearChatHistory()
    {
        lock (_historyLock)
        {
            _chatHistory.Clear();
            _displayLines.Clear();
        }

        UpdateChatView();

        var oldQueue = _inputQueue;
        _inputQueue = new BlockingCollection<string>();
        oldQueue.CompleteAdding();
    }

    private int GetDisplayWidth()
    {
        return _chatListView?.Bounds.Width ?? 80;
    }

    private void AddDisplayLines(ChatMessage msg)
    {
        var timestamp = msg.Timestamp.ToString("HH:mm");
        var messageLines = msg.Message.Split('\n');

        if (msg.IsSystem)
        {
            AddWrappedLines($"[{timestamp}] {msg.Message}", LineType.System);
        }
        else if (msg.IsToolCall)
        {
            _displayLines.Add(new ChatLine($"[{timestamp}] ══════ TOOLS CALLED ══════", LineType.ToolHeader));
            foreach (var line in messageLines)
            {
                AddWrappedLines($"        {line}", LineType.ToolContent, "        ");
            }

            _displayLines.Add(new ChatLine("        ══════════════════════════════", LineType.ToolHeader));
        }
        else if (msg.IsUser)
        {
            _displayLines.Add(new ChatLine($"[{timestamp}] >> YOU:", LineType.UserHeader));
            foreach (var line in messageLines)
            {
                AddWrappedLines($"        {line}", LineType.UserContent, "        ");
            }
        }
        else
        {
            _displayLines.Add(new ChatLine($"[{timestamp}] << {msg.Sender.ToUpperInvariant()}:", LineType.AgentHeader));
            foreach (var line in messageLines)
            {
                AddWrappedLines($"        {line}", LineType.AgentContent, "        ");
            }
        }

        _displayLines.Add(new ChatLine("", LineType.Blank));
    }

    private void AddWrappedLines(string text, LineType type, string continuationIndent = "")
    {
        var width = GetDisplayWidth();
        if (width <= 0)
        {
            width = 80;
        }

        if (text.Length <= width)
        {
            _displayLines.Add(new ChatLine(text, type));
            return;
        }

        var remaining = text;
        var isFirstLine = true;

        while (remaining.Length > 0)
        {
            var prefix = isFirstLine ? "" : continuationIndent;
            var availableWidth = width - prefix.Length;

            if (availableWidth <= 0)
            {
                availableWidth = 1;
            }

            if (remaining.Length <= availableWidth)
            {
                _displayLines.Add(new ChatLine(prefix + remaining, type));
                break;
            }

            // Find a word boundary to break at
            var breakAt = availableWidth;
            var lastSpace = remaining.LastIndexOf(' ', availableWidth - 1);
            if (lastSpace > availableWidth / 3)
            {
                breakAt = lastSpace;
            }

            var chunk = remaining[..breakAt].TrimEnd();
            _displayLines.Add(new ChatLine(prefix + chunk, type));
            remaining = remaining[breakAt..].TrimStart();
            isFirstLine = false;
        }
    }

    private void UpdateChatView()
    {
        if (_chatListView is null)
        {
            return;
        }

        Application.MainLoop?.Invoke(() =>
        {
            var dataSource = new ChatListDataSource(_displayLines);
            _chatListView.Source = dataSource;

            if (_displayLines.Count > 0)
            {
                _chatListView.SelectedItem = _displayLines.Count - 1;
                _chatListView.TopItem = Math.Max(0, _displayLines.Count - _chatListView.Bounds.Height);
            }

            _inputField?.SetFocus();
        });
    }

    public void Dispose()
    {
        _isRunning = false;
        _inputQueue.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record ChatMessage(
        string Sender,
        string Message,
        bool IsUser,
        bool IsToolCall,
        bool IsSystem,
        DateTime Timestamp);

    private sealed record ChatLine(string Text, LineType Type);

    private enum LineType
    {
        Blank,
        System,
        UserHeader,
        UserContent,
        AgentHeader,
        AgentContent,
        ToolHeader,
        ToolContent
    }

    private sealed class ChatListDataSource(IList<ChatLine> lines) : IListDataSource
    {
        public int Count => lines.Count;
        public int Length => lines.Count;

        public bool IsMarked(int item)
        {
            return false;
        }

        public void Render(ListView container, ConsoleDriver driver, bool selected, int item, int col, int row,
            int width, int start = 0)
        {
            if (item < 0 || item >= lines.Count)
            {
                return;
            }

            var line = lines[item];
            var attr = GetAttributeForLineType(line.Type, driver);

            driver.SetAttribute(attr);

            var text = line.Text.PadRight(width);
            container.Move(col, row);
            driver.AddStr(text);
        }

        public void SetMark(int item, bool value) { }

        public IList ToList()
        {
            return lines.Select(l => l.Text).ToList();
        }

        private static Attribute GetAttributeForLineType(LineType type, ConsoleDriver driver)
        {
            return type switch
            {
                LineType.System => driver.MakeAttribute(Color.BrightYellow, Color.Black),
                LineType.UserHeader => driver.MakeAttribute(Color.BrightGreen, Color.Black),
                LineType.UserContent => driver.MakeAttribute(Color.Green, Color.Black),
                LineType.AgentHeader => driver.MakeAttribute(Color.BrightCyan, Color.Black),
                LineType.AgentContent => driver.MakeAttribute(Color.Cyan, Color.Black),
                LineType.ToolHeader => driver.MakeAttribute(Color.BrightMagenta, Color.Black),
                LineType.ToolContent => driver.MakeAttribute(Color.Magenta, Color.Black),
                _ => driver.MakeAttribute(Color.White, Color.Black)
            };
        }
    }
}