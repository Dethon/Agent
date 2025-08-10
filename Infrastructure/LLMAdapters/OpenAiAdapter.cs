using System.ClientModel;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Infrastructure.LLMAdapters;

public class OpenAiClient(string endpoint, string apiKey, string[] models)
{
    private readonly IChatClient[] _openAiClients = models.Select(x =>
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint)
        };
        return new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(x)
            .AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation(configure: c =>
            {
                c.IncludeDetailedErrors = true;
                c.MaximumIterationsPerRequest = 50;
                c.AllowConcurrentInvocation = true;
                c.IncludeDetailedErrors = true;
                c.MaximumConsecutiveErrorsPerRequest = 3;
            })
            .Build();
    }).ToArray();

    public async IAsyncEnumerable<ChatResponseUpdate> Prompt(
        ImmutableList<ChatMessage> messages,
        ImmutableList<AITool> tools,
        float? temperature = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = new ChatOptions
        {
            Tools = tools,
            Temperature = temperature
        };
        var processedMessages = messages.ToList();
        foreach (var client in _openAiClients)
        {
            var updates = client.GetStreamingResponseAsync(processedMessages, options, cancellationToken);
            List<ChatResponseUpdate> processedUpdates = [];
            await foreach (var update in updates)
            {
                processedUpdates.Add(update);
                yield return update;
            }

            if (processedUpdates.All(x => x.FinishReason != ChatFinishReason.ContentFilter))
            {
                yield break;
            }

            processedMessages.AddMessages(processedUpdates);
        }
    }
}