using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Infrastructure.Agents.ChatClients;

public class OpenAiClient : DelegatingChatClient
{
    private readonly IReadOnlyList<IChatClient> _fallbackClients;
    private IEnumerable<IChatClient> AllClients => [InnerClient, .._fallbackClients];

    public OpenAiClient(string endpoint, string apiKey, string[] models, bool useFunctionInvocation = true)
        : this(
            CreateClient(endpoint, apiKey, models[0], useFunctionInvocation),
            models.Skip(1).Select(model => CreateClient(endpoint, apiKey, model, useFunctionInvocation)).ToArray())
    {
    }

    internal OpenAiClient(IChatClient primaryClient, IReadOnlyList<IChatClient> fallbackClients)
        : base(primaryClient)
    {
        _fallbackClients = fallbackClients;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var allUpdates = await GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToListAsync(cancellationToken);
        return allUpdates.ToChatResponse();
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var conversation = messages.ToList();
        foreach (var (idx, client) in AllClients.Index())
        {
            var updates = new List<ChatResponseUpdate>();
            var shouldFallback = false;
            string? fallbackReason = null;

            await using var enumerator = client
                .GetStreamingResponseAsync(conversation, options, ct)
                .GetAsyncEnumerator(ct);
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("ChatFinishReason"))
                {
                    shouldFallback = true;
                    fallbackReason = "unknown error";
                    break;
                }

                updates.Add(update);
                yield return update;

                if (update.FinishReason != ChatFinishReason.ContentFilter)
                {
                    continue;
                }

                shouldFallback = true;
                fallbackReason = "content filter";
            }

            if (!shouldFallback)
            {
                yield break;
            }

            if (TryGetFallbackMessage(idx, fallbackReason!, out var msg))
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent(msg)]
                };
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

    private bool TryGetFallbackMessage(int failedClientIndex, string reason, out string message)
    {
        message = string.Empty;
        if (failedClientIndex >= _fallbackClients.Count)
        {
            return false;
        }

        var nextModel = _fallbackClients[failedClientIndex]
            .GetService<ChatClientMetadata>()?.DefaultModelId ?? "fallback";
        message = $"\n\n_Switching to {nextModel} due to {reason}_\n\n";
        return true;
    }
}