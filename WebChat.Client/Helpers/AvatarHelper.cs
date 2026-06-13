namespace WebChat.Client.Helpers;

public static class AvatarHelper
{
    private static readonly string[] _colors =
    [
        "#E9601F",
        "#C2693B",
        "#B5611F",
        "#A8743A",
        "#9A5B4A",
        "#7C6A3F",
        "#3F7D6E",
        "#8A5A3C"
    ];

    public static string GetColorForUser(string? userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return _colors[0];
        }

        var hash = 0;
        foreach (var c in userId)
        {
            hash = (hash * 31 + c) & 0x7FFFFFFF;
        }

        return _colors[hash % _colors.Length];
    }

    public static string GetInitials(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return "?";
        }

        var words = userId.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length switch
        {
            0 => "?",
            1 => char.ToUpper(words[0][0]).ToString(),
            _ => $"{char.ToUpper(words[0][0])}{char.ToUpper(words[1][0])}"
        };
    }
}