using System.Collections;
using System.Text;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace Infrastructure.Clients.Cli;

internal sealed class ChatListDataSource(IReadOnlyList<ChatLine> lines) : IListDataSource
{
    private List<(string Text, ChatLineType Type)>? _wrappedLines;
    private int _lastWidth;

    public int Count => GetWrappedLines().Count;
    public int Length => GetWrappedLines().Count;

    public bool IsMarked(int item)
    {
        return false;
    }

    public void Render(ListView container, ConsoleDriver driver, bool selected, int item, int col, int row,
        int width, int start = 0)
    {
        var wrapped = GetWrappedLines(width);
        if (item < 0 || item >= wrapped.Count)
        {
            return;
        }

        var (text, type) = wrapped[item];
        driver.SetAttribute(GetAttributeForLineType(type, driver));

        var displayText = text.Length > width ? text[..width] : text.PadRight(width);
        container.Move(col, row);
        driver.AddStr(displayText);
    }

    public void SetMark(int item, bool value) { }

    public IList ToList()
    {
        return GetWrappedLines().Select(l => l.Text).ToList();
    }

    private List<(string Text, ChatLineType Type)> GetWrappedLines(int width = 80)
    {
        if (_wrappedLines is not null && _lastWidth == width)
        {
            return _wrappedLines;
        }

        _lastWidth = width;
        _wrappedLines = [];

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line.Text) || line.Text.Length <= width)
            {
                _wrappedLines.Add((line.Text, line.Type));
                continue;
            }

            var indent = GetIndent(line.Text);
            var wrappedTextLines = WordWrap(line.Text, width, indent);
            foreach (var wrappedLine in wrappedTextLines)
            {
                _wrappedLines.Add((wrappedLine, line.Type));
            }
        }

        return _wrappedLines;
    }

    private static string GetIndent(string text)
    {
        var indent = new StringBuilder();
        foreach (var c in text)
        {
            if (c is ' ' or '│')
            {
                indent.Append(c);
            }
            else
            {
                break;
            }
        }

        return indent.ToString();
    }

    private static List<string> WordWrap(string text, int maxWidth, string indent)
    {
        var result = new List<string>();
        var currentLine = new StringBuilder();
        var words = text.Split(' ');

        foreach (var word in words)
        {
            var testLength = currentLine.Length + (currentLine.Length > 0 ? 1 : 0) + word.Length;

            if (testLength <= maxWidth)
            {
                if (currentLine.Length > 0)
                {
                    currentLine.Append(' ');
                }

                currentLine.Append(word);
            }
            else
            {
                if (currentLine.Length > 0)
                {
                    result.Add(currentLine.ToString());
                }

                currentLine.Clear();
                currentLine.Append(indent);
                currentLine.Append(word);
            }
        }

        if (currentLine.Length > 0)
        {
            result.Add(currentLine.ToString());
        }

        return result;
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