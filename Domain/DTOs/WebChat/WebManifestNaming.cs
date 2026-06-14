namespace Domain.DTOs.WebChat;

public static class WebManifestNaming
{
    public const string BaseName = "Herfluffness' Assistants";

    public static (string Name, string ShortName) Resolve(SpaceConfig? space)
    {
        if (space is null || space.Slug == "default")
        {
            return (BaseName, BaseName);
        }

        return ($"{BaseName} — {space.Name}", space.Name);
    }
}