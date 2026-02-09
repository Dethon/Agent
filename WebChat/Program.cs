using Domain.DTOs.WebChat;
using Infrastructure.Extensions;
using JetBrains.Annotations;
using WebChat;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseDdnsIpAllowlist(app.Configuration);
app.UseBlazorFrameworkFiles();

app.MapGet("/manifest.webmanifest", (string? slug, IConfiguration config) =>
{
    const string baseName = "Herfluffness' Assistants";
    var spaces = config.GetSection("Spaces").Get<SpaceConfig[]>() ?? [];
    var space = spaces.FirstOrDefault(s => s.Slug == slug);
    var startUrl = string.IsNullOrEmpty(slug) || slug == "default" ? "/" : $"/{slug}";
    var name = space is not null && space.Slug != "default"
        ? $"{baseName} \u2014 {space.Name}"
        : baseName;
    var themeColor = space?.AccentColor ?? "#1a1a2e";

    var manifest = new
    {
        name,
        short_name = space?.Name ?? "Agent",
        start_url = startUrl,
        id = startUrl,
        display = "standalone",
        background_color = "#1a1a2e",
        theme_color = themeColor,
        prefer_related_applications = false,
        icons = new[] { new { src = "/favicon.svg", sizes = "any", type = "image/svg+xml" } }
    };

    return Results.Json(manifest, contentType: "application/manifest+json");
});

app.UseStaticFiles();

app.MapGet("/api/config", (IConfiguration config) =>
{
    var users = config.GetSection("Users").Get<UserConfig[]>() ?? [];
    return new AppConfig(
        config["AgentUrl"] ?? "http://localhost:5000",
        users);
});

app.MapFallbackToFile("index.html");

await app.RunAsync();

namespace WebChat
{
    [UsedImplicitly]
    internal record UserConfig(string Id, string AvatarUrl);

    [UsedImplicitly]
    internal record AppConfig(string AgentUrl, UserConfig[] Users);
}