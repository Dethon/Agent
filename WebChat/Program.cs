using WebChat.Extensions;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseDdnsIpAllowlist(app.Configuration);
app.UseBlazorFrameworkFiles();
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

record UserConfig(string Id, string AvatarUrl);

record AppConfig(string AgentUrl, UserConfig[] Users);