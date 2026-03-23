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

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapHub<MetricsHub>("/hubs/metrics");
app.MapMetricsApi();

app.MapFallbackToFile("index.html");

app.Run();
