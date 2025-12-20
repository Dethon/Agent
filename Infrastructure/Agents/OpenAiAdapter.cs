using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Infrastructure.Agents;

public class OpenAiClient : DelegatingChatClient
{
    private readonly IChatClient[] _fallbackClients;

    public OpenAiClient(string endpoint, string apiKey, string[] models, bool useFunctionInvocation = true)
        : base(CreateClient(endpoint, apiKey, models[0], useFunctionInvocation))
    {
        _fallbackClients = models.Skip(1)
            .Select(model => CreateClient(endpoint, apiKey, model, useFunctionInvocation))
            .ToArray();
    }

    private static IChatClient CreateClient(string endpoint, string apiKey, string model, bool useFunctionInvocation)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint)
        };
        var builder = new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(model)
            .AsIChatClient()
            .AsBuilder();

        if (useFunctionInvocation)
        {
            builder = builder.UseFunctionInvocation(configure: c =>
            {
                c.IncludeDetailedErrors = true;
                c.MaximumIterationsPerRequest = 50;
                c.AllowConcurrentInvocation = true;
                c.MaximumConsecutiveErrorsPerRequest = 3;
            });
        }

        return builder.Build();
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var processedMessages = messages.ToList();
        var response = await base.GetResponseAsync(processedMessages, options, cancellationToken);

        if (response.FinishReason != ChatFinishReason.ContentFilter)
        {
            return response;
        }

        processedMessages.AddRange(response.Messages);

        foreach (var client in _fallbackClients)
        {
            response = await client.GetResponseAsync(processedMessages, options, cancellationToken);
            if (response.FinishReason != ChatFinishReason.ContentFilter)
            {
                return response;
            }

            processedMessages.AddRange(response.Messages);
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var processedMessages = messages.ToList();
        var allClients = new[] { InnerClient }.Concat(_fallbackClients);

        foreach (var client in allClients)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in client.GetStreamingResponseAsync(processedMessages, options, ct))
            {
                updates.Add(update);
                yield return update;
            }

            if (updates.All(x => x.FinishReason != ChatFinishReason.ContentFilter))
            {
                yield break;
            }

            processedMessages.AddMessages(updates);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var client in _fallbackClients)
            {
                client.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}