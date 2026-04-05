using Microsoft.Extensions.AI;

namespace Domain.Memory;

public static class ConversationWindowRenderer
{
    public static string Render(IReadOnlyList<ChatMessage> window)
    {
        if (window.Count == 0)
        {
            return string.Empty;
        }

        var lastIndex = window.Count - 1;

        var groups = window
            .Take(lastIndex)
            .Select((msg, i) => Math.Max(1, window.Take(i + 1).Count(m => m.Role == ChatRole.User)))
            .ToArray();

        var maxGroup = groups.Length > 0 ? groups[lastIndex - 1] : 1;

        var lines = window.Select((msg, i) =>
        {
            if (i == lastIndex)
            {
                return $"[CURRENT]    {RoleLabel(msg.Role)}: {msg.Text}";
            }

            var offset = maxGroup - groups[i] + 1;
            return $"[context -{offset}] {RoleLabel(msg.Role)}: {msg.Text}";
        });

        return string.Join("\n", lines);
    }

    private static string RoleLabel(ChatRole role) =>
        role == ChatRole.Assistant ? "assistant" : "user";
}
