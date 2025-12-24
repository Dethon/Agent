using System.Collections;
using System.Text;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace Infrastructure.Clients.Cli;

internal sealed class ChatListDataSource(IReadOnlyList<ChatLine> lines, int initialWidth = 80) : IListDataSource
{
    private List<(string Text, ChatLineType Type)>? _wrappedLines;
    private int _lastWidth;
    private int _currentWidth = initialWidth;

    public int Count => GetWrappedLines(_currentWidth).Count;
    public int Length => GetWrappedLines(_currentWidth).Count;

    public bool IsMarked(int item)
    {
        return false;
    }

    public void Render(ListView container, ConsoleDriver driver, bool selected, int item, int col, int row,
        int width, int start = 0)
    {
        _currentWidth = width;
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
        return GetWrappedLines(_currentWidth).Select(l => l.Text).ToList();
    }

    private List<(string Text, ChatLineType Type)> GetWrappedLines(int width)
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

        // Trim the indent from text since we'll re-add it for wrapped lines
        var contentStart = indent.Length;
        var content = contentStart < text.Length ? text[contentStart..] : "";
        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // First line starts with original indent
        currentLine.Append(indent);

        foreach (var word in words)
        {
            var testLength = currentLine.Length + (currentLine.Length > indent.Length ? 1 : 0) + word.Length;

            if (testLength <= maxWidth)
            {
                if (currentLine.Length > indent.Length)
                {
                    currentLine.Append(' ');
                }
            }
            else
            {
                if (currentLine.Length > indent.Length)
                {
                    result.Add(currentLine.ToString());
                }

                currentLine.Clear();
                currentLine.Append(indent);
            }

            currentLine.Append(word);
        }

        if (currentLine.Length > indent.Length)
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
            ChatLineType.ToolApprovedHeader => driver.MakeAttribute(Color.BrightGreen, Color.Black),
            ChatLineType.ToolApprovedContent => driver.MakeAttribute(Color.DarkGray, Color.Black),
            ChatLineType.ToolRejectedHeader => driver.MakeAttribute(Color.BrightRed, Color.Black),
            ChatLineType.ToolRejectedContent => driver.MakeAttribute(Color.DarkGray, Color.Black),
            _ => driver.MakeAttribute(Color.White, Color.Black)
        };
    }
}