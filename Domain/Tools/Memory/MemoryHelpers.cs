using Domain.DTOs;

namespace Domain.Tools.Memory;

internal static class MemoryHelpers
{
    public static List<MemoryCategory>? ParseCategories(string? categories)
    {
        if (string.IsNullOrWhiteSpace(categories))
        {
            return null;
        }

        return categories
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => Enum.TryParse<MemoryCategory>(c, ignoreCase: true, out var cat) ? cat : (MemoryCategory?)null)
            .Where(c => c.HasValue)
            .Select(c => c!.Value)
            .ToList();
    }

    public static string TruncateContent(string content, int maxLength = 100)
    {
        return content.Length > maxLength
            ? content[..maxLength] + "..."
            : content;
    }
}