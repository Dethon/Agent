using Domain.ChatMonitor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cli.App;

public static class Monitoring
{
    public static async Task Start(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var chatMonitor = scope.ServiceProvider.GetRequiredService<ChatMonitor>();
        var lifetime = scope.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
        await chatMonitor.Monitor(lifetime.ApplicationStopping);
    }
}