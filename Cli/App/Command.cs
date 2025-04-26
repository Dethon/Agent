using Domain.Agents;
using Domain.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cli.App;

public static class Command
{
    public static async Task Start(IServiceProvider services, string prompt)
    {
        await using var scope = services.CreateAsyncScope();
        var agentResolver = scope.ServiceProvider.GetRequiredService<AgentResolver>();
        var lifetime = scope.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();

        var agent = agentResolver.Resolve(AgentType.Download);
        var responses = agent.Run(prompt, lifetime.ApplicationStopping);
        await DisplayResponses(responses, lifetime.ApplicationStopping);
    }
    
    private static async Task DisplayResponses(
        IAsyncEnumerable<AgentResponse> agentResponses, CancellationToken cancellationToken)
    {
        await foreach (var message in agentResponses.WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrEmpty(message.Reasoning))
            {
                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.WriteLine(message.Reasoning);
            }

            if (!string.IsNullOrEmpty(message.Content))
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(message.Content);
            }

            foreach (var toolCall in message.ToolCalls)
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"{toolCall.Name}({toolCall.Parameters?.ToJsonString()})");
            }
        }

        Console.ResetColor();
    }
}