namespace WebChat.Client.Helpers;

public static class AvatarHelper
{
    private static readonly string[] Colors =
    [
        "#FF6B6B",
        "#4ECDC4",
        "#45B7D1",
        "#FFA07A",
        "#98D8C8",
        "#F7DC6F",
        "#BB8FCE",
        "#85C1E2"
    ];

    public static string GetColorForUsername(string? username)
    {
        if (string.IsNullOrEmpty(username))
            return Colors[0];

        var hash = 0;
        foreach (var c in username)
        {
            hash = (hash * 31 + c) & 0x7FFFFFFF;
        }

        return Colors[hash % Colors.Length];
    }

    public static string GetInitials(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "?";

        var words = username.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
            return "?";

        if (words.Length == 1)
            return char.ToUpper(words[0][0]).ToString();

        return $"{char.ToUpper(words[0][0])}{char.ToUpper(words[1][0])}";
    }
}
