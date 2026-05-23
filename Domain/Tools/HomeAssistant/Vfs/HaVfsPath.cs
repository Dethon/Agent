namespace Domain.Tools.HomeAssistant.Vfs;

public enum HaVfsKind
{
    Root, EntitiesRoot, ClassDir, AreasRoot, AreaDir, EntityDir, StateFile, ActionFile, Unknown
}

public sealed record HaVfsNode(
    HaVfsKind Kind,
    string? ClassDomain = null,
    string? Area = null,
    string? EntityId = null,
    string? Service = null);

public static class HaVfsPath
{
    public const string StateFileName = "state.json";

    public static HaVfsNode Parse(string relativePath)
    {
        var segments = (relativePath ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return new HaVfsNode(HaVfsKind.Root);
        }

        return segments[0] switch
        {
            "entities" => ParseEntities(segments),
            "areas" => ParseAreas(segments),
            _ => new HaVfsNode(HaVfsKind.Unknown)
        };
    }

    private static HaVfsNode ParseEntities(string[] s) => s.Length switch
    {
        1 => new HaVfsNode(HaVfsKind.EntitiesRoot),
        2 => new HaVfsNode(HaVfsKind.ClassDir, ClassDomain: s[1]),
        3 => new HaVfsNode(HaVfsKind.EntityDir, ClassDomain: s[1], EntityId: $"{s[1]}.{HaSlug.StripNice(s[2])}"),
        4 => Leaf(s[3], $"{s[1]}.{HaSlug.StripNice(s[2])}", area: null),
        _ => new HaVfsNode(HaVfsKind.Unknown)
    };

    private static HaVfsNode ParseAreas(string[] s) => s.Length switch
    {
        1 => new HaVfsNode(HaVfsKind.AreasRoot),
        2 => new HaVfsNode(HaVfsKind.AreaDir, Area: s[1]),
        3 => new HaVfsNode(HaVfsKind.EntityDir, Area: s[1], EntityId: HaSlug.StripNice(s[2])),
        4 => Leaf(s[3], HaSlug.StripNice(s[2]), area: s[1]),
        _ => new HaVfsNode(HaVfsKind.Unknown)
    };

    private static HaVfsNode Leaf(string fileName, string entityId, string? area)
    {
        if (fileName.Equals(StateFileName, StringComparison.Ordinal))
        {
            return new HaVfsNode(HaVfsKind.StateFile, Area: area, EntityId: entityId);
        }
        if (fileName.EndsWith(".sh", StringComparison.Ordinal))
        {
            return new HaVfsNode(HaVfsKind.ActionFile, Area: area, EntityId: entityId, Service: fileName[..^3]);
        }
        return new HaVfsNode(HaVfsKind.Unknown);
    }
}