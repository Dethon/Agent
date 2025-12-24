using System.Collections.Concurrent;
using Domain.DTOs;
using Terminal.Gui;

namespace Infrastructure.Clients.Cli;

public sealed class TerminalGuiAdapter(string agentName) : ITerminalAdapter
{
    private static readonly TimeSpan _ctrlCTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _enterPasteBurst = TimeSpan.FromMilliseconds(75);

    private const int MinInputHeight = 3;
    private const int MaxInputHeight = 10;

    private readonly ConcurrentQueue<ChatLine> _displayLines = new();

    private ListView? _chatListView;
    private FrameView? _inputFrame;
    private TextView? _inputField;
    private bool _isRunning;
    private bool _resizeScheduled;
    private int _currentInputHeight = MinInputHeight;
    private DateTime? _lastCtrlC;
    private DateTime _lastKeyPressUtc;

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

    public void ShowToolResult(string toolName, IReadOnlyDictionary<string, object?> arguments,
        ToolApprovalResult resultType)
    {
        var lines = ChatMessageFormatter.FormatToolResult(toolName, arguments, resultType).ToArray();
        DisplayMessage(lines);
    }

    public Task<ToolApprovalResult> ShowApprovalDialogAsync(string toolName, string details,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ToolApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        Application.MainLoop?.Invoke(() =>
        {
            var result = ApprovalDialog.Show(toolName, details);
            registration.Dispose();
            tcs.TrySetResult(result);
            _inputField?.SetFocus();
        });

        return tcs.Task;
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
        var (inputFrame, inputField) = CliUiFactory.CreateInputArea();
        _inputFrame = inputFrame;
        _chatListView = CliUiFactory.CreateChatListView(inputFrame);
        _chatListView.Source = new ChatListDataSource(_displayLines.ToArray());
        _inputField = inputField;

        _inputField.KeyPress += HandleKeyPress;

        mainWindow.Add(titleBar, statusBar, _chatListView, inputFrame);
        Application.Top.Add(mainWindow);
        Application.Top.Loaded += () =>
        {
            _inputField.SetFocus();
            ScheduleInputResize();
        };
        _inputField.SetFocus();
        ScheduleInputResize();

        ShowSystemMessage("Welcome! Type a message to start chatting.");

        Application.Run();
        Application.Shutdown();
    }

    private void HandleKeyPress(View.KeyEventEventArgs args)
    {
        var now = DateTime.UtcNow;
        var burst = now - _lastKeyPressUtc < _enterPasteBurst;

        try
        {
            switch (args.KeyEvent.Key)
            {
                case Key.Enter | Key.ShiftMask:
                {
                    _inputField?.InsertText("\n");
                    args.Handled = true;
                    break;
                }
                case Key.Enter:
                {
                    // During paste, many Enter key events arrive in a burst; treat them as newlines.
                    if (burst)
                    {
                        _inputField?.InsertText("\n");
                        args.Handled = true;
                        break;
                    }

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
        finally
        {
            _lastKeyPressUtc = now;
            ScheduleInputResize();
        }
    }

    private void ScheduleInputResize()
    {
        if (_resizeScheduled || _inputFrame is null)
        {
            return;
        }

        _resizeScheduled = true;
        Application.MainLoop?.AddIdle(() =>
        {
            _resizeScheduled = false;
            UpdateInputLayout();
            return false;
        });
    }

    private void UpdateInputLayout()
    {
        if (_inputFrame is null || _inputField is null)
        {
            return;
        }

        var frameWidth = _inputFrame.Bounds.Width - 2;
        if (frameWidth <= 0)
        {
            frameWidth = 80;
        }

        var text = _inputField.Text.ToString() ?? "";
        var visualLines = 0;

        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrEmpty(line))
            {
                visualLines++;
                continue;
            }

            visualLines += (int)Math.Ceiling((double)line.Length / frameWidth);
        }

        visualLines = Math.Max(1, visualLines);
        var newHeight = Math.Clamp(visualLines + 2, MinInputHeight, MaxInputHeight);

        if (_currentInputHeight == newHeight)
        {
            return;
        }

        _currentInputHeight = newHeight;
        _inputFrame.Height = newHeight;
        _inputFrame.Y = Pos.AnchorEnd(newHeight + 1);

        Application.Top?.LayoutSubviews();
        Application.Top?.SetNeedsDisplay();
        _chatListView?.SetNeedsDisplay();
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

            if (_inputField is { Selecting: true })
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
            var width = _chatListView.Bounds.Width;
            var dataSource = new ChatListDataSource(snapshot, width > 0 ? width : 80);
            _chatListView.Source = dataSource;

            if (_chatListView.Source.Count > 0)
            {
                _chatListView.SelectedItem = _chatListView.Source.Count - 1;
                _chatListView.TopItem = Math.Max(0, _chatListView.Source.Count - _chatListView.Bounds.Height);
            }

            _inputField?.SetFocus();
        });
    }
}