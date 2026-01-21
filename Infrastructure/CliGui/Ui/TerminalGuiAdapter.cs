using System.Collections.Concurrent;
using Domain.DTOs;
using Infrastructure.CliGui.Abstractions;
using Infrastructure.CliGui.Rendering;
using Terminal.Gui;

namespace Infrastructure.CliGui.Ui;

public sealed class TerminalGuiAdapter(string agentName) : ITerminalAdapter
{
    private readonly CollapseStateManager _collapseState = new();

    private readonly ConcurrentQueue<ChatLine> _displayLines = new();

    private ListView? _chatListView;
    private int _currentInputHeight = Ui.MinInputHeight;
    private ColorScheme? _inputColorScheme;
    private TextView? _inputField;
    private FrameView? _inputFrame;
    private ColorScheme? _inputHintColorScheme;

    private bool _isRunning;
    private bool _isThinking;
    private DateTime? _lastCtrlCUtc;
    private DateTime _lastKeyPressUtc;

    private Action<MouseEvent>? _previousRootMouseEvent;
    private bool _resizeScheduled;
    private string _savedInputText = "";
    private Label? _statusBar;
    private ThinkingIndicator? _thinkingIndicator;

    public event Action<string>? InputReceived;
    public event Action? ShutdownRequested;

    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;

        new Thread(RunTerminalGui) { IsBackground = true }.Start();
        Thread.Sleep(500);
    }

    public void Stop()
    {
        _isRunning = false;
        Application.RootMouseEvent = _previousRootMouseEvent;
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
        DisplayMessage(ChatMessageFormatter.FormatMessage(chatMessage).ToArray());
    }

    public void ShowToolResult(string toolName, IReadOnlyDictionary<string, object?> arguments,
        ToolApprovalResult resultType)
    {
        DisplayMessage(ChatMessageFormatter.FormatToolResult(toolName, arguments, resultType).ToArray());
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

    public void ShowThinkingIndicator()
    {
        _isThinking = true;
        _thinkingIndicator?.Show();
        Application.MainLoop?.Invoke(() =>
        {
            UpdateStatusBar(CliUiFactory.StatusBarThinking);
            ShowInputHint();
        });
    }

    public void HideThinkingIndicator()
    {
        _isThinking = false;
        _thinkingIndicator?.Hide();
        Application.MainLoop?.Invoke(() =>
        {
            UpdateStatusBar(CliUiFactory.StatusBarDefault);
            HideInputHint();
        });
    }

    public void Dispose()
    {
        _thinkingIndicator?.Dispose();
        Stop();
    }

    private void ShowInputHint()
    {
        if (_inputField is null)
        {
            return;
        }

        _savedInputText = _inputField.Text?.ToString() ?? "";
        _inputField.Text = CliUiFactory.InputHintThinking;
        _inputField.ColorScheme = _inputHintColorScheme;
        _inputField.SetNeedsDisplay();
    }

    private void HideInputHint()
    {
        if (_inputField is null)
        {
            return;
        }

        _inputField.Text = _savedInputText;
        _inputField.ColorScheme = _inputColorScheme;
        _inputField.SetNeedsDisplay();
        _savedInputText = "";
    }

    private void UpdateStatusBar(string text)
    {
        _statusBar?.Text = text;
    }

    private void RunTerminalGui()
    {
        Application.Init();
        InitializeUi();

        ShowSystemMessage("Welcome! Type a message to start chatting.");

        Application.Run();
        Application.Shutdown();
    }

    private void InitializeUi()
    {
        var baseScheme = CliUiFactory.CreateBaseScheme();
        var (mainWindow, titleBar, statusBar, chatListView, inputFrame, inputField) = CreateViews(baseScheme);

        _chatListView = chatListView;
        _inputFrame = inputFrame;
        _inputField = inputField;
        _statusBar = (Label)statusBar;
        _thinkingIndicator = new ThinkingIndicator((Label)titleBar, agentName);
        _inputColorScheme = inputField.ColorScheme;
        _inputHintColorScheme = new ColorScheme
        {
            Normal = Application.Driver.MakeAttribute(Color.DarkGray, Color.Black),
            Focus = Application.Driver.MakeAttribute(Color.DarkGray, Color.Black)
        };

        WireEvents();
        WireRootMouseEvents();

        mainWindow.Add(titleBar, statusBar, chatListView, inputFrame);
        Application.Top.Add(mainWindow);

        Application.Top.Loaded += () =>
        {
            _inputField.SetFocus();
            ScheduleInputResize();
        };

        _inputField.SetFocus();
        ScheduleInputResize();
    }

    private (
        Window MainWindow,
        View TitleBar,
        View StatusBar,
        ListView ChatListView,
        FrameView InputFrame,
        TextView InputField)
        CreateViews(ColorScheme baseScheme)
    {
        var mainWindow = CliUiFactory.CreateMainWindow(baseScheme);
        var titleBar = CliUiFactory.CreateTitleBar(agentName);
        var statusBar = CliUiFactory.CreateStatusBar();
        var (inputFrame, inputField) = CliUiFactory.CreateInputArea();

        var chatListView = CliUiFactory.CreateChatListView(inputFrame);
        chatListView.Source = new ChatListDataSource(_displayLines.ToArray(), _collapseState);

        return (mainWindow, titleBar, statusBar, chatListView, inputFrame, inputField);
    }

    private void WireEvents()
    {
        if (_inputField is null)
        {
            return;
        }

        _inputField.KeyPress += HandleKeyPress;
        _chatListView!.KeyPress += HandleChatListKeyPress;
        _chatListView.MouseClick += HandleChatListMouseClick;
    }

    private void WireRootMouseEvents()
    {
        _previousRootMouseEvent = Application.RootMouseEvent;
        Application.RootMouseEvent = me =>
        {
            _previousRootMouseEvent?.Invoke(me);
            HandleRootMouseEvent(me);
        };
    }

    private void HandleRootMouseEvent(MouseEvent me)
    {
        if (_chatListView?.Source is not ChatListDataSource dataSource)
        {
            return;
        }

        var flags = me.Flags;
        if (!flags.HasFlag(MouseFlags.Button1Clicked))
        {
            return;
        }

        var viewPoint = _chatListView.ScreenToView(me.X, me.Y);
        if (viewPoint.X < 0 || viewPoint.Y < 0
                            || viewPoint.X >= _chatListView.Bounds.Width
                            || viewPoint.Y >= _chatListView.Bounds.Height)
        {
            return;
        }

        var wrappedIndex = _chatListView.TopItem + viewPoint.Y;
        if (wrappedIndex < 0 || wrappedIndex >= _chatListView.Source.Count)
        {
            return;
        }

        _chatListView.SetFocus();
        _chatListView.SelectedItem = wrappedIndex;

        var sourceLine = dataSource.GetSourceLineAt(wrappedIndex);
        if (sourceLine is not { IsCollapsible: true, GroupId: not null })
        {
            return;
        }

        _collapseState.ToggleGroup(sourceLine.GroupId);
        dataSource.InvalidateCache();
        _chatListView.SetNeedsDisplay();
    }

    private void HandleChatListMouseClick(View.MouseEventArgs args)
    {
        if (_chatListView is null)
        {
            return;
        }

        var flags = args.MouseEvent.Flags;
        if (!flags.HasFlag(MouseFlags.Button1Clicked))
        {
            return;
        }

        if (_chatListView.Source is not ChatListDataSource dataSource)
        {
            return;
        }

        _chatListView.SetFocus();

        var viewPoint = _chatListView.ScreenToView(args.MouseEvent.X, args.MouseEvent.Y);
        var wrappedIndex = _chatListView.TopItem + viewPoint.Y;

        if (wrappedIndex < 0 || wrappedIndex >= _chatListView.Source.Count)
        {
            return;
        }

        _chatListView.SelectedItem = wrappedIndex;

        var sourceLine = dataSource.GetSourceLineAt(wrappedIndex);
        if (sourceLine is not { IsCollapsible: true, GroupId: not null })
        {
            return;
        }

        _collapseState.ToggleGroup(sourceLine.GroupId);
        dataSource.InvalidateCache();
        _chatListView.SetNeedsDisplay();

        args.Handled = true;
    }

    private void HandleChatListKeyPress(View.KeyEventEventArgs args)
    {
        // Space to toggle collapsible groups (reasoning)
        if (args.KeyEvent.Key is not Key.Space)
        {
            return;
        }

        if (_chatListView?.Source is not ChatListDataSource dataSource)
        {
            return;
        }

        var sourceLine = dataSource.GetSourceLineAt(_chatListView.SelectedItem);
        if (sourceLine is not { IsCollapsible: true, GroupId: not null })
        {
            return;
        }

        _collapseState.ToggleGroup(sourceLine.GroupId);
        dataSource.InvalidateCache();
        _chatListView.SetNeedsDisplay();
        args.Handled = true;
    }

    private void HandleKeyPress(View.KeyEventEventArgs args)
    {
        var now = DateTime.UtcNow;
        var isBurst = now - _lastKeyPressUtc < Ui.EnterPasteBurst;

        try
        {
            switch (args.KeyEvent.Key)
            {
                case Key.Esc:
                    HandleEscape(args);
                    return;
                case Key.Enter | Key.ShiftMask:
                    if (!_isThinking)
                    {
                        InsertNewline(args);
                    }
                    else
                    {
                        args.Handled = true;
                    }

                    return;
                case Key.Enter:
                    if (!_isThinking)
                    {
                        HandleEnter(isBurst, args);
                    }
                    else
                    {
                        args.Handled = true;
                    }

                    return;
                case Key.C | Key.CtrlMask:
                    HandleCtrlC();
                    args.Handled = true;
                    return;
            }

            // Block all other input while thinking
            if (_isThinking)
            {
                args.Handled = true;
            }
        }
        finally
        {
            _lastKeyPressUtc = now;
            ScheduleInputResize();
        }
    }

    private void HandleEnter(bool treatAsNewline, View.KeyEventEventArgs args)
    {
        if (_inputField is null)
        {
            return;
        }

        // During paste, many Enter key events arrive in a burst; treat them as newlines.
        if (treatAsNewline)
        {
            InsertNewline(args);
            return;
        }

        var input = _inputField.Text?.ToString()?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            args.Handled = true;
            return;
        }

        InputReceived?.Invoke(input);
        _inputField.Text = "";
        args.Handled = true;
    }

    private void InsertNewline(View.KeyEventEventArgs args)
    {
        _inputField?.InsertText("\n");
        args.Handled = true;

        // Reset scroll position after inserting newline - the ScheduleInputResize in finally block
        // will resize the input, but we need to ensure scroll is reset when content fits
        ResetInputScrollIfContentFits();
    }

    private void ResetInputScrollIfContentFits()
    {
        if (_inputFrame is null || _inputField is null)
        {
            return;
        }

        var frameWidth = _inputFrame.Bounds.Width - 2;
        if (frameWidth <= 0)
        {
            frameWidth = Ui.DefaultWidth;
        }

        var text = _inputField.Text.ToString() ?? "";
        var visualLines = ComputeVisualLineCount(text, frameWidth);

        // Available height inside input field (frame height minus borders)
        var availableHeight = _currentInputHeight - 2;

        // If all content fits, reset scroll to top
        if (visualLines <= availableHeight)
        {
            _inputField.TopRow = 0;
            _inputField.SetNeedsDisplay();
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
            frameWidth = Ui.DefaultWidth;
        }

        var visualLines = ComputeVisualLineCount(_inputField.Text.ToString() ?? "", frameWidth);
        var newHeight = Math.Clamp(visualLines + 2, Ui.MinInputHeight, Ui.MaxInputHeight);
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

        // Reset scroll position if content now fits after resize
        ResetInputScrollIfContentFits();
    }

    private static int ComputeVisualLineCount(string text, int frameWidth)
    {
        if (frameWidth <= 0)
        {
            return 1;
        }

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

        return Math.Max(1, visualLines);
    }

    private void HandleCtrlC()
    {
        var now = DateTime.UtcNow;

        if (_lastCtrlCUtc.HasValue && now - _lastCtrlCUtc.Value < Ui.CtrlCConfirmTimeout)
        {
            _isRunning = false;
            Application.RequestStop();
            ShutdownRequested?.Invoke();
            return;
        }

        _lastCtrlCUtc = now;

        if (_inputField is { Selecting: true })
        {
            _inputField.Copy();
        }

        ShowSystemMessage("Press Ctrl+C again to exit.");
    }

    private void HandleEscape(View.KeyEventEventArgs args)
    {
        if (_isThinking)
        {
            // Cancel the current operation (same as /cancel)
            InputReceived?.Invoke("/cancel");
        }
        else if (_inputField is not null)
        {
            // Clear the input field when not thinking
            _inputField.Text = "";
        }

        args.Handled = true;
    }

    private void UpdateChatView()
    {
        if (_chatListView is null)
        {
            return;
        }

        var snapshot = _displayLines.ToArray();
        Application.MainLoop?.Invoke(() => UpdateChatViewCore(snapshot));
    }

    private void UpdateChatViewCore(ChatLine[] snapshot)
    {
        if (_chatListView is null)
        {
            return;
        }

        var width = _chatListView.Bounds.Width;
        _chatListView.Source = new ChatListDataSource(snapshot, _collapseState, width > 0 ? width : Ui.DefaultWidth);

        if (_chatListView.Source.Count > 0)
        {
            _chatListView.SelectedItem = _chatListView.Source.Count - 1;
            _chatListView.TopItem = Math.Max(0, _chatListView.Source.Count - _chatListView.Bounds.Height);
        }

        _inputField?.SetFocus();
    }

    private static class Ui
    {
        public const int DefaultWidth = 80;
        public const int MinInputHeight = 3; // 1 text line + frame borders
        public const int MaxInputHeight = 10;

        public static readonly TimeSpan CtrlCConfirmTimeout = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan EnterPasteBurst = TimeSpan.FromMilliseconds(75);
    }
}