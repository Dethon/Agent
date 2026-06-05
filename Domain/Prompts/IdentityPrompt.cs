namespace Domain.Prompts;

public static class IdentityPrompt
{
    public static string Build(string name, string? description) =>
        string.IsNullOrWhiteSpace(description)
            ? $"## Identity\n\nYou are {name}."
            : $"## Identity\n\nYou are {name}. {description}";
}