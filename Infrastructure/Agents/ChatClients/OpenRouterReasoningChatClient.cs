using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

public sealed class OpenRouterReasoningChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var queue = new ConcurrentQueue<string>();
        OpenRouterReasoningTap.CurrentQueue.Value = queue;

        try
        {
            await foreach (var update in InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                var tapped = Drain(queue);
                if (!string.IsNullOrWhiteSpace(tapped))
                {
                    TryAppendReasoning(update, tapped);
                }

                yield return update;
            }
        }
        finally
        {
            OpenRouterReasoningTap.CurrentQueue.Value = null;
        }
    }

    private static void TryAppendReasoning(ChatResponseUpdate update, string reasoning)
    {
        if (string.IsNullOrWhiteSpace(reasoning))
        {
            return;
        }

        update.Contents.Add(new TextReasoningContent(reasoning));
    }

    private static string Drain(ConcurrentQueue<string> queue)
    {
        if (queue.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        while (queue.TryDequeue(out var chunk))
        {
            sb.Append(chunk);
        }

        return sb.ToString();
    }
}