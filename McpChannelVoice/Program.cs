using McpChannelVoice.Modules;
using McpChannelVoice.Services;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetVoiceSettings();
builder.Services.ConfigureVoiceChannel(settings);

if (settings.Announce.BindToLoopbackOnly)
{
    builder.WebHost.UseKestrel(options =>
        options.Listen(System.Net.IPAddress.Loopback, 8080));
}

var app = builder.Build();
app.MapMcp("/mcp");
AnnounceEndpoint.Map(app);

await app.RunAsync();