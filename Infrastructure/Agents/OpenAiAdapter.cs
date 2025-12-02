using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Infrastructure.Agents;

public class OpenAiClient(string endpoint, string apiKey, string[] models) : IChatClient
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

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var processedMessages = messages.ToList();
        foreach (var client in _openAiClients)
        {
            var response = await client.GetResponseAsync(processedMessages, options, cancellationToken);
            if (response.FinishReason != ChatFinishReason.ContentFilter)
            {
                return response;
            }

            processedMessages.AddRange(response.Messages);
        }

        return await _openAiClients[^1].GetResponseAsync(processedMessages, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return _openAiClients[0].GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        foreach (var client in _openAiClients)
        {
            client.Dispose();
        }
    }
}