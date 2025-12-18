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
    private static readonly TimeSpan CtrlCTimeout = TimeSpan.FromSeconds(2);

    private readonly string _agentName;
    private readonly Action? _onShutdownRequested;
    private readonly List<ChatMessage> _chatHistory = [];
    private readonly List<ChatLine> _displayLines = [];
    private readonly object _historyLock = new();

    private BlockingCollection<string> _inputQueue = new();
    private ListView? _chatListView;
    private TextField? _inputField;
    private Window? _mainWindow;
    private int _messageCounter;
    private bool _isRunning;
    private bool _headerDisplayed;
    private DateTime? _lastCtrlC;

    public CliChatMessengerClient(string agentName, Action? onShutdownRequested = null)
    {
        _agentName = agentName;
        _onShutdownRequested = onShutdownRequested;
    }

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_headerDisplayed)
        {
            StartTerminalGui();
            _headerDisplayed = true;
        }

        cancellationToken.Register(() =>
        {
            _isRunning = false;
            Application.MainLoop?.Invoke(() => Application.RequestStop());
        });

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

            var baseScheme = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.White, Color.Black),
                HotNormal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black),
                HotFocus = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black)
            };

            _mainWindow = new Window
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                Border = new Border { BorderStyle = BorderStyle.None },
                ColorScheme = baseScheme
            };

            var titleBar = new Label($" ◆ {_agentName}")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                ColorScheme = new ColorScheme
                {
                    Normal = Application.Driver.MakeAttribute(Color.Black, Color.BrightCyan)
                }
            };

            var statusBar = new Label(" ⌨ /help  ◦  ⌫ /clear  ◦  ↑↓ scroll")
            {
                X = 0,
                Y = 1,
                Width = Dim.Fill(),
                ColorScheme = new ColorScheme
                {
                    Normal = Application.Driver.MakeAttribute(Color.Gray, Color.DarkGray)
                }
            };

            _chatListView = new ListView(new ChatListDataSource(_displayLines))
            {
                X = 1,
                Y = 3,
                Width = Dim.Fill() - 2,
                Height = Dim.Fill() - 6,
                AllowsMarking = false,
                CanFocus = false,
                ColorScheme = new ColorScheme
                {
                    Normal = Application.Driver.MakeAttribute(Color.White, Color.Black),
                    Focus = Application.Driver.MakeAttribute(Color.White, Color.Black)
                }
            };

            var inputFrame = new FrameView
            {
                X = 1,
                Y = Pos.Bottom(_chatListView) + 1,
                Width = Dim.Fill() - 2,
                Height = 3,
                ColorScheme = new ColorScheme
                {
                    Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                    Focus = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black)
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
                else if (args.KeyEvent.Key == (Key.C | Key.CtrlMask))
                {
                    HandleCtrlC();
                    args.Handled = true;
                }
            };

            inputFrame.Add(_inputField);

            _mainWindow.Add(titleBar, statusBar, _chatListView, inputFrame);

            Application.Top.Add(_mainWindow);
            Application.Top.Loaded += () => _inputField.SetFocus();
            _inputField.SetFocus();

            AddToHistory("[System]", "Welcome! Type a message to start chatting.", isUser: false, isSystem: true);

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
                AddToHistory("[Help]", "  Ctrl+C twice  - Exit application", isUser: false, isSystem: true);
                break;

            default:
                _inputQueue.Add(input);
                break;
        }
    }

    private void HandleCtrlC()
    {
        var now = DateTime.UtcNow;

        if (_lastCtrlC.HasValue && now - _lastCtrlC.Value < CtrlCTimeout)
        {
            RequestShutdown();
        }
        else
        {
            _lastCtrlC = now;

            // Try default copy behavior first
            if (_inputField?.SelectedLength > 0)
            {
                _inputField.Copy();
            }

            AddToHistory("[System]", "Press Ctrl+C again to exit.", isUser: false, isSystem: true);
        }
    }

    private void RequestShutdown()
    {
        _isRunning = false;
        _inputQueue.CompleteAdding();
        Application.RequestStop();
        _onShutdownRequested?.Invoke();
    }

    private void AddToHistory(string sender, string message, bool isUser, bool isToolCall = false,
        bool isSystem = false)
    {
        var chatMessage = new ChatMessage(sender, message, isUser, isToolCall, isSystem, DateTime.Now);

        lock (_historyLock)
        {
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

    private void AddDisplayLines(ChatMessage msg)
    {
        var timestamp = msg.Timestamp.ToString("HH:mm");
        var messageLines = msg.Message.Split('\n');

        if (msg.IsSystem)
        {
            _displayLines.Add(new ChatLine($"  ○ {msg.Message}", LineType.System));
        }
        else if (msg.IsToolCall)
        {
            _displayLines.Add(new ChatLine("  ┌─ ⚡ Tools ─────────────────", LineType.ToolHeader));
            foreach (var line in messageLines)
            {
                _displayLines.Add(new ChatLine($"  │  {line}", LineType.ToolContent));
            }

            _displayLines.Add(new ChatLine("  └──────────────────────────────", LineType.ToolHeader));
        }
        else if (msg.IsUser)
        {
            _displayLines.Add(new ChatLine($"  ▶ You · {timestamp}", LineType.UserHeader));
            foreach (var line in messageLines)
            {
                _displayLines.Add(new ChatLine($"    {line}", LineType.UserContent));
            }
        }
        else
        {
            _displayLines.Add(new ChatLine($"  ◀ {msg.Sender} · {timestamp}", LineType.AgentHeader));
            foreach (var line in messageLines)
            {
                _displayLines.Add(new ChatLine($"    {line}", LineType.AgentContent));
            }
        }

        _displayLines.Add(new ChatLine("", LineType.Blank));
    }

    private void UpdateChatView()
    {
        if (_chatListView is null)
        {
            return;
        }

        Application.MainLoop?.Invoke(() =>
        {
            _chatListView.Source = new ChatListDataSource(_displayLines);

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
            driver.SetAttribute(GetAttributeForLineType(line.Type, driver));

            var text = line.Text.Length > width ? line.Text[..width] : line.Text.PadRight(width);
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