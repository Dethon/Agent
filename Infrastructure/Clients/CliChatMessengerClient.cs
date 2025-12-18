using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Cli;
using Terminal.Gui;

namespace Infrastructure.Clients;

public class CliChatMessengerClient : IChatMessengerClient, IDisposable
{
    private const long DefaultChatId = 1;
    private const int DefaultThreadId = 1;
    private static readonly TimeSpan _ctrlCTimeout = TimeSpan.FromSeconds(2);

    private readonly string _agentName;
    private readonly Action? _onShutdownRequested;
    private readonly CliCommandHandler _commandHandler;

    private ConcurrentQueue<ChatLine> _displayLines = new();

    private BlockingCollection<string> _inputQueue = new();
    private ListView? _chatListView;
    private TextField? _inputField;
    private int _messageCounter;
    private bool _isRunning;
    private bool _headerDisplayed;
    private DateTime? _lastCtrlC;

    public CliChatMessengerClient(string agentName, Action? onShutdownRequested = null)
    {
        _agentName = agentName;
        _onShutdownRequested = onShutdownRequested;
        _commandHandler = new CliCommandHandler(ClearChatHistory, AddToHistory);
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
            string? input;
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

            var baseScheme = CliUiFactory.CreateBaseScheme();
            var mainWindow = CliUiFactory.CreateMainWindow(baseScheme);
            var titleBar = CliUiFactory.CreateTitleBar(_agentName);
            var statusBar = CliUiFactory.CreateStatusBar();
            _chatListView = CliUiFactory.CreateChatListView(_displayLines.ToArray());
            var (inputFrame, inputField) = CliUiFactory.CreateInputArea(_chatListView);
            _inputField = inputField;

            _inputField.KeyPress += args =>
            {
                switch (args.KeyEvent.Key)
                {
                    case Key.Enter:
                    {
                        var input = _inputField.Text?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(input))
                        {
                            ProcessInput(input);
                            _inputField.Text = "";
                        }

                        args.Handled = true;
                        break;
                    }
                    case Key.C | Key.CtrlMask:
                        HandleCtrlC();
                        args.Handled = true;
                        break;
                }
            };

            mainWindow.Add(titleBar, statusBar, _chatListView, inputFrame);
            Application.Top.Add(mainWindow);
            Application.Top.Loaded += () => _inputField.SetFocus();
            _inputField.SetFocus();

            AddToHistory("[System]", "Welcome! Type a message to start chatting.", false, false, true);

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
        if (!_commandHandler.TryHandleCommand(input))
        {
            _inputQueue.Add(input);
        }
    }

    private void HandleCtrlC()
    {
        var now = DateTime.UtcNow;

        if (_lastCtrlC.HasValue && now - _lastCtrlC.Value < _ctrlCTimeout)
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

        foreach (var line in ChatMessageFormatter.FormatMessage(chatMessage))
        {
            _displayLines.Enqueue(line);
        }

        UpdateChatView();
    }

    private void ClearChatHistory()
    {
        _displayLines = new ConcurrentQueue<ChatLine>();

        UpdateChatView();

        // Send /cancel to trigger agent cancellation before clearing the queue
        _inputQueue.Add("/cancel");

        var oldQueue = _inputQueue;
        _inputQueue = new BlockingCollection<string>();
        oldQueue.CompleteAdding();
    }

    private void UpdateChatView()
    {
        if (_chatListView is null)
        {
            return;
        }

        var snapshot = _displayLines.ToArray();

        Application.MainLoop?.Invoke(() =>
        {
            _chatListView.Source = new ChatListDataSource(snapshot);

            if (_chatListView.Source.Count > 0)
            {
                _chatListView.SelectedItem = _chatListView.Source.Count - 1;
                _chatListView.TopItem = Math.Max(0, _chatListView.Source.Count - _chatListView.Bounds.Height);
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
}