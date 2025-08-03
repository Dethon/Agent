using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cli.App;

public static class Command
{
    public static async Task Start(IServiceProvider services, string prompt)
    {
        await using var scope = services.CreateAsyncScope();
        var lifetime = scope.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
        var agentResolver = scope.ServiceProvider.GetRequiredService<IAgentResolver>();

        var agent = await agentResolver.Resolve(AgentType.Download);
        var responses = agent.Run(prompt, true, lifetime.ApplicationStopping);
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

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"StopReason: {message.StopReason}");

            foreach (var toolCall in message.ToolCalls)
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine(toolCall.ToString());
            }
        }

        Console.ResetColor();
    }
}