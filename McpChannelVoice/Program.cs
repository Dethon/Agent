using McpChannelVoice.Modules;
using McpChannelVoice.Services;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetVoiceSettings();
builder.Services.ConfigureVoiceChannel(settings);

var app = builder.Build();
app.MapMcp("/mcp");
AnnounceEndpoint.Map(app);
DismissEndpoint.Map(app);
SatellitesEndpoint.Map(app);

await app.RunAsync();