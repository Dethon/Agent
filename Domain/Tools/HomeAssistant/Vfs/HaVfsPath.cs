namespace Domain.Tools.HomeAssistant.Vfs;

public enum HaVfsKind
{
    Root, EntitiesRoot, ClassDir, AreasRoot, AreaDir, EntityDir, StateFile, ActionFile, Unknown
}

public sealed record HaVfsNode(
    HaVfsKind Kind,
    string? ClassDomain = null,
    string? Area = null,
    string? EntitySegment = null,
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
        3 => new HaVfsNode(HaVfsKind.EntityDir, ClassDomain: s[1], EntitySegment: s[2]),
        4 => Leaf(s[3], classDomain: s[1], area: null, segment: s[2]),
        _ => new HaVfsNode(HaVfsKind.Unknown)
    };

    private static HaVfsNode ParseAreas(string[] s) => s.Length switch
    {
        1 => new HaVfsNode(HaVfsKind.AreasRoot),
        2 => new HaVfsNode(HaVfsKind.AreaDir, Area: s[1]),
        3 => new HaVfsNode(HaVfsKind.EntityDir, Area: s[1], EntitySegment: s[2]),
        4 => Leaf(s[3], classDomain: null, area: s[1], segment: s[2]),
        _ => new HaVfsNode(HaVfsKind.Unknown)
    };

    private static HaVfsNode Leaf(string fileName, string? classDomain, string? area, string segment)
    {
        if (fileName.Equals(StateFileName, StringComparison.Ordinal))
        {
            return new HaVfsNode(HaVfsKind.StateFile, ClassDomain: classDomain, Area: area, EntitySegment: segment);
        }
        if (fileName.EndsWith(".sh", StringComparison.Ordinal))
        {
            return new HaVfsNode(HaVfsKind.ActionFile, ClassDomain: classDomain, Area: area, EntitySegment: segment, Service: fileName[..^3]);
        }
        return new HaVfsNode(HaVfsKind.Unknown);
    }
}