using Dashboard.Client;
using Dashboard.Client.Effects;
using Dashboard.Client.Services;
using Dashboard.Client.State.Connection;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Latency;
using Dashboard.Client.State.Memory;
using Dashboard.Client.State.Metrics;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ =>
    new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Stores
builder.Services.AddSingleton<MetricsStore>();
builder.Services.AddSingleton<HealthStore>();
builder.Services.AddSingleton<TokensStore>();
builder.Services.AddSingleton<ToolsStore>();
builder.Services.AddSingleton<ErrorsStore>();
builder.Services.AddSingleton<SchedulesStore>();
builder.Services.AddSingleton<ConnectionStore>();
builder.Services.AddSingleton<MemoryStore>();
builder.Services.AddSingleton<LatencyStore>();

// Services
builder.Services.AddScoped<MetricsApiService>();
builder.Services.AddScoped<LocalStorageService>();
builder.Services.AddSingleton(sp =>
{
    var nav = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    var hubUrl = new Uri(nav.ToAbsoluteUri("/hubs/metrics").ToString());
    return new MetricsHubService(hubUrl);
});

// Effects
builder.Services.AddScoped<DataLoadEffect>();
builder.Services.AddScoped<MetricsHubEffect>();

await builder.Build().RunAsync();