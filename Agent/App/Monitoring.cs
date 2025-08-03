using Domain.Monitor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agent.App;

public static class Monitoring
{
    public static async Task Start(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var lifetime = scope.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
        var chatMonitor = scope.ServiceProvider.GetRequiredService<ChatMonitor>();
        await chatMonitor.Monitor(lifetime.ApplicationStopping);
    }
}