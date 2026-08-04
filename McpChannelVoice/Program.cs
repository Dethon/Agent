using Mcp.Hosting;
using McpChannelVoice.Modules;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<VoiceSettings>().WithResolvedLocalityDefaults();
builder.Services.ConfigureVoiceChannel(settings);

var app = builder.Build();
app.MapMcp("/mcp");
AnnounceEndpoint.Map(app);
DismissEndpoint.Map(app);
SatellitesEndpoint.Map(app);

await app.RunAsync();