using System.Collections;
using System.Text;
using Infrastructure.CliGui.Rendering;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace Infrastructure.CliGui.Ui;

internal sealed class ChatListDataSource : IListDataSource
{
    private readonly IReadOnlyList<ChatLine> _lines;
    private readonly CollapseStateManager _collapseState;

    private List<WrappedLine>? _wrappedLines;
    private int _lastWidth;
    private int _currentWidth;

    public ChatListDataSource(
        IReadOnlyList<ChatLine> lines,
        CollapseStateManager collapseState,
        int initialWidth = 80)
    {
        _lines = lines;
        _collapseState = collapseState;
        _currentWidth = initialWidth;

        InitializeCollapseState();
    }

    public int Count => GetWrappedLines(_currentWidth).Count;
    public int Length => GetWrappedLines(_currentWidth).Count;

    public ChatLine? GetSourceLineAt(int wrappedIndex)
    {
        var wrapped = GetWrappedLines(_currentWidth);
        if (wrappedIndex < 0 || wrappedIndex >= wrapped.Count)
        {
            return null;
        }

        return wrapped[wrappedIndex].SourceLine;
    }

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

        var line = wrapped[item];
        driver.SetAttribute(GetAttributeForLineType(line.Type, driver));

        var displayText = line.Text.Length > width ? line.Text[..width] : line.Text.PadRight(width);
        container.Move(col, row);
        driver.AddStr(displayText);
    }

    public void SetMark(int item, bool value) { }

    public IList ToList()
    {
        return GetWrappedLines(_currentWidth).Select(l => l.Text).ToList();
    }

    public void InvalidateCache()
    {
        _wrappedLines = null;
    }

    private void InitializeCollapseState()
    {
        foreach (var line in _lines)
        {
            if (line is { IsCollapsible: true, GroupId: not null })
            {
                _collapseState.SetCollapsed(line.GroupId, true);
            }
        }
    }

    private List<WrappedLine> GetWrappedLines(int width)
    {
        if (_wrappedLines is not null && _lastWidth == width)
        {
            return _wrappedLines;
        }

        _lastWidth = width;
        _wrappedLines = [];

        foreach (var line in _lines)
        {
            if (ShouldSkipLine(line))
            {
                continue;
            }

            var displayText = GetDisplayText(line);

            if (string.IsNullOrEmpty(displayText) || displayText.Length <= width)
            {
                _wrappedLines.Add(new WrappedLine(displayText, line.Type, line));
                continue;
            }

            var indent = GetIndent(displayText);
            var wrappedTextLines = WordWrap(displayText, width, indent);
            foreach (var wrappedText in wrappedTextLines)
            {
                _wrappedLines.Add(new WrappedLine(wrappedText, line.Type, line));
            }
        }

        return _wrappedLines;
    }

    private bool ShouldSkipLine(ChatLine line)
    {
        if (line.GroupId is null || line.IsCollapsible)
        {
            return false;
        }

        return _collapseState.IsCollapsed(line.GroupId);
    }

    private string GetDisplayText(ChatLine line)
    {
        if (!line.IsCollapsible || line.GroupId is null)
        {
            return line.Text;
        }

        var isCollapsed = _collapseState.IsCollapsed(line.GroupId);
        return isCollapsed
            ? line.Text.Replace("▼", "▶")
            : line.Text.Replace("▶", "▼");
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

        var contentStart = indent.Length;
        var content = contentStart < text.Length ? text[contentStart..] : "";
        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);

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
            ChatLineType.ReasoningHeader => driver.MakeAttribute(Color.DarkGray, Color.Black),
            ChatLineType.ReasoningContent => driver.MakeAttribute(Color.DarkGray, Color.Black),
            _ => driver.MakeAttribute(Color.White, Color.Black)
        };
    }

    private sealed record WrappedLine(string Text, ChatLineType Type, ChatLine SourceLine);
}