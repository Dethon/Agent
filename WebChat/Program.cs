var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGet("/api/config", (IConfiguration config) => new
{
    AgentUrl = config["AgentUrl"] ?? "http://localhost:5000"
});

app.MapFallbackToFile("index.html");

await app.RunAsync();