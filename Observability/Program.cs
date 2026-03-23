using Observability;
using Observability.Hubs;
using Observability.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>();

var redisConnection = builder.Configuration["Redis:ConnectionString"]
    ?? throw new InvalidOperationException("Redis:ConnectionString is required");

builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnection));

builder.Services.AddSignalR();
builder.Services.AddSingleton<MetricsQueryService>();
builder.Services.AddHostedService<MetricsCollectorService>();

var app = builder.Build();

app.UsePathBase("/dashboard");
app.UseRouting();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGet("/manifest.webmanifest", () =>
{
    var manifest = new
    {
        name = "Agent Dashboard",
        short_name = "Dashboard",
        start_url = "/dashboard/",
        id = "/dashboard/",
        scope = "/dashboard/",
        display = "standalone",
        background_color = "#1a1a2e",
        theme_color = "#1a1a2e",
        prefer_related_applications = false,
        icons = new[]
        {
            new { src = "favicon.svg", sizes = "any", type = "image/svg+xml" }
        }
    };
    return Results.Json(manifest, contentType: "application/manifest+json");
});

app.MapHub<MetricsHub>("/hubs/metrics");
app.MapMetricsApi();

app.MapFallbackToFile("index.html");

app.Run();
