using Domain.Contracts;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Clients.Voice;
using Infrastructure.Timers;
using Infrastructure.Utils;
using McpServerTimers.McpPrompts;
using McpServerTimers.McpResources;
using McpServerTimers.Services;
using McpServerTimers.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServerTimers.Modules;

public static class ConfigModule
{
    public static TimerSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        return config.Get<TimerSettings>()
               ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigureTimers(this IServiceCollection services, TimerSettings settings)
    {
        services.AddHttpClient(VoiceHubHttp.ClientName, client =>
        {
            client.BaseAddress = new Uri(settings.VoiceHub.BaseUrl);
            // Bound the wait: the hub returns 202 for announce (ringing is async) and resolves/dismisses
            // instantly, so a slow response means the hub is unhealthy. Fail fast rather than block the
            // once-per-second fire loop (or an agent's fs_create) on the default 100s timeout.
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services
            .AddSingleton(settings)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ITimerStore, InMemoryTimerStore>()
            // The three hub-local capabilities reached over HTTP: fire (announce), stop (dismiss),
            // and target resolution/roster (satellites). The adapters create the named client per
            // call, so the factory's handler rotation works despite their singleton lifetimes.
            .AddSingleton<IInsistentAnnouncer>(sp => new HttpInsistentAnnouncer(sp.GetRequiredService<IHttpClientFactory>(), settings.Announce.Token))
            .AddSingleton<IAlertDismisser>(sp => new HttpAlertDismisser(sp.GetRequiredService<IHttpClientFactory>(), settings.Announce.Token))
            .AddSingleton<ISatelliteCatalog>(sp => new HttpSatelliteCatalog(sp.GetRequiredService<IHttpClientFactory>(), settings.Announce.Token))
            .AddSingleton(sp => new TimerFileSystem(
                sp.GetRequiredService<ITimerStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IAlertDismisser>(),
                sp.GetRequiredService<ISatelliteCatalog>()))
            .AddHostedService<TimerFireService>();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .AddFileSystemTools<TimerFileSystem>()
            .WithResources<FileSystemResource>()
            .WithPrompts<TimersSystemPrompt>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    var logger = context.Services?.GetRequiredService<ILogger<Program>>();
                    logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                    return ToolResponse.Create(ex);
                }
            }));

        return services;
    }
}