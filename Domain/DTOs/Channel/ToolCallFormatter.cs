using System.Text;
using System.Text.Json;
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public static class ToolCallFormatter
{
    public static string Format(string jsonContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;
            var toolName = root.GetProperty("Name").GetString()?.Split(':').LastOrDefault() ?? "unknown";
            var sb = new StringBuilder();
            sb.AppendLine($"\ud83d\udd27 {toolName}");

            if (!root.TryGetProperty("Arguments", out var args) || args.ValueKind != JsonValueKind.Object)
            {
                return sb.ToString().TrimEnd();
            }

            foreach (var prop in args.EnumerateObject())
            {
                var val = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.GetRawText();
                if (val.Length > 100)
                {
                    val = val[..100] + "...";
                }

                sb.AppendLine($"  {prop.Name}: {val}");
            }

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return jsonContent;
        }
    }
}
