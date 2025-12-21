using System.Collections.Concurrent;
using Domain.DTOs;
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
                Width = Dim.Percent(70),
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
            var buttons = new Button[3];

            var approveBtn = new Button("_Approve")
            {
                X = Pos.Center() - 20,
                Y = Pos.AnchorEnd(1),
                ColorScheme = CreateButtonScheme(true)
            };
            buttons[0] = approveBtn;

            var alwaysBtn = new Button("A_lways")
            {
                X = Pos.Center() - 5,
                Y = Pos.AnchorEnd(1),
                ColorScheme = CreateButtonScheme(false)
            };
            buttons[1] = alwaysBtn;

            var rejectBtn = new Button("_Reject")
            {
                X = Pos.Center() + 10,
                Y = Pos.AnchorEnd(1),
                ColorScheme = CreateButtonScheme(false)
            };
            buttons[2] = rejectBtn;

            var dialogResult = ToolApprovalResult.Rejected;

            approveBtn.Clicked += () => closeDialog(ToolApprovalResult.Approved);
            alwaysBtn.Clicked += () => closeDialog(ToolApprovalResult.ApprovedAndRemember);
            rejectBtn.Clicked += () => closeDialog(ToolApprovalResult.Rejected);

            for (var i = 0; i < buttons.Length; i++)
            {
                var index = i;
                buttons[i].Enter += _ =>
                {
                    selectedIndex = index;
                    updateButtonStyles();
                };
            }

            dialog.KeyPress += args =>
            {
                switch (args.KeyEvent.Key)
                {
                    case Key.CursorLeft:
                    case Key.CursorUp:
                        selectedIndex = Math.Max(0, selectedIndex - 1);
                        updateButtonStyles();
                        buttons[selectedIndex].SetFocus();
                        args.Handled = true;
                        break;

                    case Key.CursorRight:
                    case Key.CursorDown:
                    case Key.Tab:
                        selectedIndex = Math.Min(buttons.Length - 1, selectedIndex + 1);
                        updateButtonStyles();
                        buttons[selectedIndex].SetFocus();
                        args.Handled = true;
                        break;

                    case Key.a:
                    case Key.A:
                        closeDialog(ToolApprovalResult.Approved);
                        args.Handled = true;
                        break;

                    case Key.l:
                    case Key.L:
                        closeDialog(ToolApprovalResult.ApprovedAndRemember);
                        args.Handled = true;
                        break;

                    case Key.r:
                    case Key.R:
                    case Key.Esc:
                        closeDialog(ToolApprovalResult.Rejected);
                        args.Handled = true;
                        break;
                }
            };

            dialog.Add(toolLabel, toolNameLabel, detailsView, approveBtn, alwaysBtn, rejectBtn);
            approveBtn.SetFocus();
            Application.Run(dialog);

            // Set result after dialog closes
            tcs.TrySetResult(dialogResult);

            // Restore focus to input field after dialog closes
            _inputField?.SetFocus();
            return;

            void updateButtonStyles()
            {
                for (var i = 0; i < buttons.Length; i++)
                {
                    buttons[i].ColorScheme = CreateButtonScheme(selectedIndex == i);
                }
            }

            void closeDialog(ToolApprovalResult result)
            {
                dialogResult = result;
                registration.Dispose();
                Application.RequestStop();
            }
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