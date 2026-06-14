namespace Domain.DTOs.WebChat;

public static class WebManifestNaming
{
    public const string BaseName = "Herfluffness' Assistants";

    public static (string Name, string ShortName) Resolve(SpaceConfig? space)
    {
        var name = space is not null && space.Slug != "default"
            ? $"{BaseName} — {space.Name}"
            : BaseName;
        var shortName = space?.Name ?? BaseName;
        return (name, shortName);
    }
}