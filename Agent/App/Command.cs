using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agent.App;

public static class Command
{
    public static async Task Start(IServiceProvider services, string prompt, AgentSettings settings)
    {
        await using var scope = services.CreateAsyncScope();
        var lifetime = scope.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
       
        ChatMessage[] messages = [
            ..DownloaderPrompt.Get(),
            new(ChatRole.User, prompt)
        ];
        var agent = await Domain.Agents.Agent.CreateAsync(
            settings.McpServers.Select(x => x.Endpoint).ToArray(),
            (message, _) =>
            {
                DisplayResponses(message);
                return Task.CompletedTask;
            },
            scope.ServiceProvider.GetRequiredService<ILargeLanguageModel>(),
            lifetime.ApplicationStopping);
        
        await agent.Run(messages, lifetime.ApplicationStopping);
        
        await Task.Delay(Int32.MaxValue, lifetime.ApplicationStopping);
    }

    private static void DisplayResponses(ChatMessage message)
    {
        Console.WriteLine(message.Text);
        // if (!string.IsNullOrEmpty(message.Reasoning))
        // {
        //     Console.ForegroundColor = ConsoleColor.DarkBlue;
        //     Console.WriteLine(message.Reasoning);
        // }
        //
        // if (!string.IsNullOrEmpty(message.Content))
        // {
        //     Console.ForegroundColor = ConsoleColor.White;
        //     Console.WriteLine(message.Content);
        // }
        //
        // Console.ForegroundColor = ConsoleColor.DarkGray;
        // Console.WriteLine($"StopReason: {message.StopReason}");
        //
        // foreach (var toolCall in message.ToolCalls)
        // {
        //     Console.ForegroundColor = ConsoleColor.DarkCyan;
        //     Console.WriteLine(toolCall.ToString());
        // }
    }
}