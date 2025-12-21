using System.Collections.Concurrent;
using Terminal.Gui;

namespace Infrastructure.Clients.Cli;

public sealed class TerminalGuiAdapter(string agentName) : ITerminalAdapter
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

    public void ShowAutoApprovedTool(string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        var lines = ChatMessageFormatter.FormatAutoApprovedTool(toolName, arguments).ToArray();
        DisplayMessage(lines);
    }

    public Task<bool> ShowApprovalDialogAsync(string toolName, string details, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        Application.MainLoop?.Invoke(() =>
        {
            var dialogScheme = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.White, Color.Black),
                HotNormal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black),
                HotFocus = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black)
            };

            var contentHeight = Math.Min(details.Split('\n').Length + 6, 15);

            var dialog = new Dialog
            {
                Title = "🔧 Approval Required",
                Width = Dim.Percent(60),
                Height = contentHeight,
                ColorScheme = dialogScheme,
                Border = new Border
                {
                    BorderStyle = BorderStyle.Rounded,
                    BorderBrush = Color.BrightCyan
                }
            };

            var toolLabel = new Label("Tool: ")
            {
                X = 1,
                Y = 0,
                ColorScheme = new ColorScheme
                {
                    Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black)
                }
            };

            var toolNameLabel = new Label(toolName)
            {
                X = Pos.Right(toolLabel),
                Y = 0,
                ColorScheme = new ColorScheme
                {
                    Normal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black)
                }
            };

            var detailsView = new TextView
            {
                X = 1,
                Y = 2,
                Width = Dim.Fill(1),
                Height = Dim.Fill(2),
                ReadOnly = true,
                Text = details,
                CanFocus = false,
                ColorScheme = new ColorScheme
                {
                    Normal = Application.Driver.MakeAttribute(Color.DarkGray, Color.Black),
                    Focus = Application.Driver.MakeAttribute(Color.DarkGray, Color.Black)
                }
            };

            var selectedIndex = 0;

            var approveBtn = new Button("_Approve")
            {
                X = Pos.Center() - 12,
                Y = Pos.AnchorEnd(1),
                ColorScheme = CreateButtonScheme(true)
            };

            var rejectBtn = new Button("_Reject")
            {
                X = Pos.Center() + 3,
                Y = Pos.AnchorEnd(1),
                ColorScheme = CreateButtonScheme(false)
            };

            void UpdateButtonStyles()
            {
                approveBtn.ColorScheme = CreateButtonScheme(selectedIndex == 0);
                rejectBtn.ColorScheme = CreateButtonScheme(selectedIndex == 1);
            }

            var dialogResult = false;

            void CloseDialog(bool result)
            {
                dialogResult = result;
                registration.Dispose();
                Application.RequestStop();
            }

            approveBtn.Clicked += () => CloseDialog(true);
            rejectBtn.Clicked += () => CloseDialog(false);

            approveBtn.Enter += _ =>
            {
                selectedIndex = 0;
                UpdateButtonStyles();
            };

            rejectBtn.Enter += _ =>
            {
                selectedIndex = 1;
                UpdateButtonStyles();
            };

            dialog.KeyPress += args =>
            {
                switch (args.KeyEvent.Key)
                {
                    case Key.CursorLeft:
                    case Key.CursorUp:
                        selectedIndex = 0;
                        UpdateButtonStyles();
                        approveBtn.SetFocus();
                        args.Handled = true;
                        break;

                    case Key.CursorRight:
                    case Key.CursorDown:
                    case Key.Tab:
                        selectedIndex = 1;
                        UpdateButtonStyles();
                        rejectBtn.SetFocus();
                        args.Handled = true;
                        break;

                    case Key.a:
                    case Key.A:
                        CloseDialog(true);
                        args.Handled = true;
                        break;

                    case Key.r:
                    case Key.R:
                    case Key.Esc:
                        CloseDialog(false);
                        args.Handled = true;
                        break;
                }
            };

            dialog.Add(toolLabel, toolNameLabel, detailsView, approveBtn, rejectBtn);
            approveBtn.SetFocus();
            Application.Run(dialog);

            // Set result after dialog closes
            tcs.TrySetResult(dialogResult);

            // Restore focus to input field after dialog closes
            _inputField?.SetFocus();
        });

        return tcs.Task;
    }

    private static ColorScheme CreateButtonScheme(bool isSelected)
    {
        return isSelected
            ? new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.Black, Color.BrightCyan),
                Focus = Application.Driver.MakeAttribute(Color.Black, Color.BrightCyan),
                HotNormal = Application.Driver.MakeAttribute(Color.White, Color.BrightCyan),
                HotFocus = Application.Driver.MakeAttribute(Color.White, Color.BrightCyan)
            }
            : new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.White, Color.Black),
                HotNormal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black),
                HotFocus = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black)
            };
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