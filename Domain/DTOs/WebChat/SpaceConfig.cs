using System.Text.RegularExpressions;

namespace Domain.DTOs.WebChat;

public partial record SpaceConfig(string Slug, string Name, string AccentColor)
{
    private static readonly Regex _slugPattern = SpaceSlugRegex();

    public static bool IsValidSlug(string? slug) => slug is not null && _slugPattern.IsMatch(slug);
    
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled)]
    private static partial Regex SpaceSlugRegex();
}
