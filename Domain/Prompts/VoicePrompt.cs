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
            "These are the voice satellites you can be heard on — the spoken devices placed around the home. Each entry is a stable satellite id and the room it's in:",
            "",
            .. satellites.Select(s => $"- {s.Id} — {s.Location}"),
            "",
            "Each incoming message tells you which satellite and room it came from, so you can tailor answers to where the person is."
        ];

        return string.Join("\n", sections);
    }
}