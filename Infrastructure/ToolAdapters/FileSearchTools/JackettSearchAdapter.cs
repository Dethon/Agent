using System.Text.Json.Nodes;
using Domain.Tools;

namespace Infrastructure.ToolAdapters.FileSearchTools;

public class JackettSearchAdapter(HttpClient client) : FileSearchTool
{
    protected override async Task<JsonNode> Resolve(FileSearchParams parameters, CancellationToken cancellationToken)
    {
        await Task.Delay(1000, cancellationToken);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File search completed successfully",
            ["totalResults"] = 3,
            ["results"] = new JsonArray
            {
                new JsonObject
                {
                    ["title"] = "jurasic park t-rex movie",
                    ["url"] = "https://example.com/file1.mp4",
                    ["size"] = 1024000000,
                    ["seeders"] = 150,
                    ["leechers"] = 25,
                    ["date"] = "2023-10-15T14:30:00Z"
                },
                new JsonObject
                {
                    ["title"] = "origami dinosaur diagram pack",
                    ["url"] = "https://example.com/file2.pdf",
                    ["size"] = 2048000,
                    ["seeders"] = 75,
                    ["leechers"] = 12,
                    ["date"] = "2023-10-10T09:15:00Z"
                },
                new JsonObject
                {
                    ["title"] = "origami animals",
                    ["url"] = "https://example.com/file3.pdf",
                    ["size"] = 3072000,
                    ["seeders"] = 220,
                    ["leechers"] = 45,
                    ["date"] = "2023-10-05T18:45:00Z"
                }
            }
        };
    }
}