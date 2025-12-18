using System.Collections;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace Infrastructure.Clients.Cli;

internal sealed class ChatListDataSource(IReadOnlyList<ChatLine> lines) : IListDataSource
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

    private static Attribute GetAttributeForLineType(ChatLineType type, ConsoleDriver driver)
    {
        return type switch
        {
            ChatLineType.System => driver.MakeAttribute(Color.BrightYellow, Color.Black),
            ChatLineType.UserHeader => driver.MakeAttribute(Color.BrightGreen, Color.Black),
            ChatLineType.UserContent => driver.MakeAttribute(Color.Green, Color.Black),
            ChatLineType.AgentHeader => driver.MakeAttribute(Color.BrightCyan, Color.Black),
            ChatLineType.AgentContent => driver.MakeAttribute(Color.Cyan, Color.Black),
            ChatLineType.ToolHeader => driver.MakeAttribute(Color.BrightMagenta, Color.Black),
            ChatLineType.ToolContent => driver.MakeAttribute(Color.Magenta, Color.Black),
            _ => driver.MakeAttribute(Color.White, Color.Black)
        };
    }
}