using Terminal.Gui;

namespace Infrastructure.Clients.Cli;

internal static class CliUiFactory
{
    public static ColorScheme CreateBaseScheme()
    {
        return new ColorScheme
        {
            Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
            Focus = Application.Driver.MakeAttribute(Color.White, Color.Black),
            HotNormal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black),
            HotFocus = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black)
        };
    }

    public static Window CreateMainWindow(ColorScheme baseScheme)
    {
        return new Window
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Border = new Border { BorderStyle = BorderStyle.None },
            ColorScheme = baseScheme
        };
    }

    public static Label CreateTitleBar(string agentName)
    {
        return new Label($" ◆ {agentName}")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            ColorScheme = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.Black, Color.BrightCyan)
            }
        };
    }

    public static Label CreateStatusBar()
    {
        return new Label(" ⌨ /help  ◦  ⌫ /clear  ◦  × /cancel  ◦  ↑↓ scroll")
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            ColorScheme = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.Gray, Color.DarkGray)
            }
        };
    }

    private const int MinInputHeight = 3;

    public static (FrameView frame, TextView input) CreateInputArea()
    {
        var inputFrame = new FrameView
        {
            X = 1,
            Y = Pos.AnchorEnd(MinInputHeight + 1),
            Width = Dim.Fill() - 2,
            Height = MinInputHeight,
            ColorScheme = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black)
            }
        };

        var inputField = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            WordWrap = true,
            ColorScheme = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.White, Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.White, Color.Black)
            }
        };


        inputFrame.Add(inputField);
        return (inputFrame, inputField);
    }

    public static ListView CreateChatListView(FrameView inputFrame)
    {
        return new ListView
        {
            X = 1,
            Y = 3,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - Dim.Height(inputFrame) - 5,
            AllowsMarking = false,
            CanFocus = false,
            ColorScheme = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.White, Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.White, Color.Black)
            }
        };
    }
}