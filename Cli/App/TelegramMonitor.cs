using Domain.Agents;
using Domain.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cli.App;

public class TelegramMonitor(IServiceProvider services, TaskQueue queue, ILogger<TelegramMonitor> logger)
{
    private readonly ILogger<TelegramMonitor> _logger = logger;

    public async Task Monitor()
    {
        await using var scope = services.CreateAsyncScope();
        var lifetime = scope.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            var prompt = ""; // TODO: Get prompt from Telegram message
            await queue.QueueTask((c) => AgentTask(prompt, c));
        }
    }

    private async Task AgentTask(string prompt, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var agentResolver = scope.ServiceProvider.GetRequiredService<AgentResolver>();
        var agent = agentResolver.Resolve(AgentType.Download);
        var responses = agent.Run(prompt, cancellationToken);
        await foreach (var response in responses)
        {
            await SendTelegramMessage(response, cancellationToken);
        }
    }

    private async Task SendTelegramMessage(AgentResponse response, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}