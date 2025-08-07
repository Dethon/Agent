using System.ClientModel;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI;

namespace Infrastructure.LLMAdapters;

public class OpenAiAdapter(string endpoint, string apiKey, string model) : ILargeLanguageModel
{
    private readonly IChatClient _openAiClient = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint)
            })
        .GetChatClient(model)
        .AsIChatClient()
        .AsBuilder()
        .UseFunctionInvocation(configure: c =>
        {
            c.AllowConcurrentInvocation = true;
        })
        .Build();

    public IAsyncEnumerable<ChatResponseUpdate> Prompt(
        IEnumerable<ChatMessage> messages, 
        IEnumerable<McpClientTool> tools, 
        float? temperature = null, 
        CancellationToken cancellationToken = default)
    {
        return _openAiClient.GetStreamingResponseAsync(
            messages,
            new ChatOptions
            {
                Tools = [.. tools],
                Temperature = temperature
            }, cancellationToken);
    }
}