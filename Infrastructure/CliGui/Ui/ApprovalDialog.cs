using Domain.DTOs;
using Terminal.Gui;

namespace Infrastructure.CliGui.Ui;

internal static class ApprovalDialog
{
    public static ToolApprovalResult Show(string toolName, string details)
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
        var dialogResult = ToolApprovalResult.Rejected;

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

        return dialogResult;

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
            Application.RequestStop();
        }
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
}