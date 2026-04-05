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

        // Assign a 1-based user-turn group to each non-current message.
        // The group is the count of user messages in positions 0..i, clamped to min 1.
        // This means pre-first-user messages share group 1 with the first user message.
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
