namespace Domain.Prompts;

public static class VoicePrompt
{
    public const string Name = "voice_prompt";

    public const string Description =
        "Lists the available voice satellites (id + room) the agent can be heard on";

    public static string Build(IReadOnlyList<(string Id, string Location)> satellites)
    {
        if (satellites.Count == 0)
        {
            return string.Empty;
        }

        string[] sections =
        [
            "## Voice satellites",
            "",
            "These are the satellites you can be heard on — each entry is a stable satellite id and the room it's in:",
            "",
            .. satellites.Select(s => $"- {s.Id} — {s.Location}"),
            "",
            "Each incoming message tells you which satellite and room it came from. Use it silently — act on it only when the room changes the answer, such as which device to control — and never mention the satellite or the room in your reply."
        ];

        return string.Join("\n", sections);
    }
}