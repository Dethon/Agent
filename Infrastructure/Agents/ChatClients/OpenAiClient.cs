using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Infrastructure.Agents.ChatClients;

public class OpenAiClient : DelegatingChatClient
{
    private readonly IReadOnlyList<IChatClient> _fallbackClients;

    public OpenAiClient(string endpoint, string apiKey, string[] models, bool useFunctionInvocation = true)
        : base(CreateClient(endpoint, apiKey, models[0], useFunctionInvocation))
    {
        _fallbackClients = models.Skip(1)
            .Select(model => CreateClient(endpoint, apiKey, model, useFunctionInvocation))
            .ToArray();
    }

    private IEnumerable<IChatClient> AllClients => [InnerClient, .. _fallbackClients];

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var conversation = messages.ToList();

        foreach (var client in AllClients)
        {
            var response = await client.GetResponseAsync(conversation, options, cancellationToken);
            if (!WasContentFiltered(response))
            {
                return response;
            }

            conversation.AddRange(response.Messages);
        }

        return await InnerClient.GetResponseAsync(conversation, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var conversation = messages.ToList();

        foreach (var client in AllClients)
        {
            var (updates, wasFiltered) = await StreamAndCollectAsync(client, conversation, options, ct);
            foreach (var update in updates)
            {
                yield return update;
            }

            if (!wasFiltered)
            {
                yield break;
            }

            conversation.AddMessages(updates);
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

    private static IChatClient CreateClient(string endpoint, string apiKey, string model, bool useFunctionInvocation)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
        var builder = new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(model)
            .AsIChatClient()
            .AsBuilder();

        if (useFunctionInvocation)
        {
            builder = builder.UseFunctionInvocation(configure: opts =>
            {
                opts.IncludeDetailedErrors = true;
                opts.MaximumIterationsPerRequest = 50;
                opts.AllowConcurrentInvocation = true;
                opts.MaximumConsecutiveErrorsPerRequest = 3;
            });
        }

        return builder.Build();
    }

    private static bool WasContentFiltered(ChatResponse response)
    {
        return response.FinishReason == ChatFinishReason.ContentFilter;
    }

    private static bool WasContentFiltered(IEnumerable<ChatResponseUpdate> updates)
    {
        return updates.Any(u => u.FinishReason == ChatFinishReason.ContentFilter);
    }

    private static async Task<(List<ChatResponseUpdate> Updates, bool WasFiltered)> StreamAndCollectAsync(
        IChatClient client,
        List<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken ct)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
        {
            updates.Add(update);
        }

        return (updates, WasContentFiltered(updates));
    }
}