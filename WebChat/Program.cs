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

    var iconColor = Uri.EscapeDataString(space?.AccentColor ?? SpaceConfig.DefaultAccentColor);
    var manifest = new
    {
        name,
        short_name = space?.Name ?? "Agent",
        start_url = startUrl,
        id = startUrl,
        scope = startUrl,
        display = "standalone",
        background_color = "#1a1a2e",
        theme_color = themeColor,
        prefer_related_applications = false,
        icons = new[] { new { src = $"/icon.svg?color={iconColor}", sizes = "any", type = "image/svg+xml" } }
    };

    return Results.Json(manifest, contentType: "application/manifest+json");
});

app.MapGet("/icon.svg", (string? color) =>
{
    var fill = SpaceConfig.IsValidHexColor(color) ? color! : SpaceConfig.DefaultAccentColor;
    var svg = $"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 50" width="120" height="50">
            <text x="60" y="38" text-anchor="middle" font-family="Arial, sans-serif" font-size="40" fill="{fill}">ᓚᘏᗢ</text>
        </svg>
        """;
    return Results.Text(svg, "image/svg+xml");
});

app.UseStaticFiles();

app.MapGet("/api/config", (IConfiguration config) =>
{
    var users = config.GetSection("Users").Get<UserConfig[]>() ?? [];
    var vapidPublicKey = config["WebPush:PublicKey"];
    return new AppConfig(
        config["AgentUrl"] ?? "http://localhost:5000",
        users,
        vapidPublicKey);
});

app.MapGet("/api/spaces/{slug}", (string slug, IConfiguration config) =>
{
    if (!SpaceConfig.IsValidSlug(slug))
    {
        return Results.NotFound();
    }

    var spaces = config.GetSection("Spaces").Get<SpaceConfig[]>() ?? [];
    var space = spaces.FirstOrDefault(s => s.Slug == slug);
    return space is not null ? Results.Ok(space) : Results.NotFound();
});

app.MapFallbackToFile("index.html");

await app.RunAsync();

namespace WebChat
{
    [UsedImplicitly]
    internal record UserConfig(string Id, string AvatarUrl);

    [UsedImplicitly]
    internal record AppConfig(string AgentUrl, UserConfig[] Users, string? VapidPublicKey);
}