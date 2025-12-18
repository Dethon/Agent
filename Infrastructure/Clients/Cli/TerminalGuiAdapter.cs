using System.Collections.Concurrent;
using Terminal.Gui;

namespace Infrastructure.Clients.Cli;

internal sealed class TerminalGuiAdapter(string agentName) : ITerminalAdapter
{
    private static readonly TimeSpan _ctrlCTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentQueue<ChatLine> _displayLines = new();

    private ListView? _chatListView;
    private TextField? _inputField;
    private bool _isRunning;
    private DateTime? _lastCtrlC;

    public event Action<string>? InputReceived;
    public event Action? ShutdownRequested;

    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;

        var guiThread = new Thread(RunTerminalGui)
        {
            IsBackground = true
        };

        guiThread.Start();
        Thread.Sleep(500);
    }

    public void Stop()
    {
        _isRunning = false;
        Application.MainLoop?.Invoke(() => Application.RequestStop());
    }

    public void DisplayMessage(ChatLine[] lines)
    {
        foreach (var line in lines)
        {
            _displayLines.Enqueue(line);
        }

        UpdateChatView();
    }

    public void ClearDisplay()
    {
        while (_displayLines.TryDequeue(out _)) { }

        UpdateChatView();
    }

    public void ShowSystemMessage(string message)
    {
        var chatMessage = new ChatMessage("[System]", message, false, false, true, DateTime.Now);
        var lines = ChatMessageFormatter.FormatMessage(chatMessage).ToArray();
        DisplayMessage(lines);
    }

    public void Dispose()
    {
        Stop();
    }

    private void RunTerminalGui()
    {
        Application.Init();

        var baseScheme = CliUiFactory.CreateBaseScheme();
        var mainWindow = CliUiFactory.CreateMainWindow(baseScheme);
        var titleBar = CliUiFactory.CreateTitleBar(agentName);
        var statusBar = CliUiFactory.CreateStatusBar();
        _chatListView = CliUiFactory.CreateChatListView(_displayLines.ToArray());
        var (inputFrame, inputField) = CliUiFactory.CreateInputArea(_chatListView);
        _inputField = inputField;

        _inputField.KeyPress += HandleKeyPress;

        mainWindow.Add(titleBar, statusBar, _chatListView, inputFrame);
        Application.Top.Add(mainWindow);
        Application.Top.Loaded += () => _inputField.SetFocus();
        _inputField.SetFocus();

        ShowSystemMessage("Welcome! Type a message to start chatting.");

        Application.Run();
        Application.Shutdown();
    }

    private void HandleKeyPress(View.KeyEventEventArgs args)
    {
        switch (args.KeyEvent.Key)
        {
            case Key.Enter:
            {
                var input = _inputField?.Text?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(input))
                {
                    InputReceived?.Invoke(input);
                    _inputField!.Text = "";
                }

                args.Handled = true;
                break;
            }
            case Key.C | Key.CtrlMask:
                HandleCtrlC();
                args.Handled = true;
                break;
        }
    }

    private void HandleCtrlC()
    {
        var now = DateTime.UtcNow;

        if (_lastCtrlC.HasValue && now - _lastCtrlC.Value < _ctrlCTimeout)
        {
            _isRunning = false;
            Application.RequestStop();
            ShutdownRequested?.Invoke();
        }
        else
        {
            _lastCtrlC = now;

            if (_inputField?.SelectedLength > 0)
            {
                _inputField.Copy();
            }

            ShowSystemMessage("Press Ctrl+C again to exit.");
        }
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
}