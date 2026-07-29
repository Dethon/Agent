using System.Text.Json;
using System.Threading.Channels;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Clients.Channels;

public sealed class McpChannelConnection(string channelId, bool attachOnly = false, ILogger<McpChannelConnection>? logger = null)
    : IChannelConnection, IMcpChannelConnection, IAsyncDisposable
{
    private const string CancelCommandContent = "/cancel";

    private readonly Channel<ChannelMessage> _messageChannel = Channel.CreateUnbounded<ChannelMessage>();
    private McpClient? _client;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    public string ChannelId { get; } = channelId;

    public bool AttachOnly { get; } = attachOnly;

    public IAsyncEnumerable<ChannelMessage> Messages => _messageChannel.Reader.ReadAllAsync();

    public async Task ConnectAsync(string endpoint, CancellationToken ct)
    {
        _client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(endpoint) }),
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = $"{ChannelProtocol.ChannelClientNamePrefix}{ChannelId}",
                    Version = "1.0.0"
                }
            },
            cancellationToken: ct);

        _pumpCts = new CancellationTokenSource();
        _pumpTask = PumpAsync(_pumpCts.Token);
    }

    // Inbound items are pulled, not pushed: a stateless server cannot address a session, so the
    // agent long-polls channel_receive and feeds the two notification handlers itself.
    private async Task PumpAsync(CancellationToken ct)
    {
        var subscriberId = $"{ChannelProtocol.ChannelClientNamePrefix}{ChannelId}";
        var backoff = TimeSpan.FromSeconds(1);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var call = await _client!.CallToolAsync(
                    ChannelProtocol.ReceiveTool,
                    new Dictionary<string, object?>
                    {
                        ["subscriberId"] = subscriberId,
                        ["maxWaitMs"] = ChannelProtocol.DefaultReceiveWaitMs
                    },
                    cancellationToken: ct);

                var text = call.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
                if (call.IsError == true || string.IsNullOrEmpty(text))
                {
                    // Not a batch: re-polling straight away would spin the loop hot against a
                    // server that keeps answering this way, so take the back-off path.
                    throw new InvalidOperationException($"{ChannelProtocol.ReceiveTool} returned no batch: {text}");
                }

                var batch = JsonSerializer.Deserialize<ChannelReceiveResult>(text, ChannelProtocol.SerializerOptions);
                foreach (var item in batch?.Items ?? [])
                {
                    Dispatch(item);
                }

                // An empty batch is the normal timeout outcome, so re-poll immediately; the
                // back-off below is reserved for the failure path.
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "channel_receive failed on {ChannelId}; retrying", ChannelId);
                try
                {
                    await Task.Delay(backoff, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    private void Dispatch(ChannelInboxItem item)
    {
        if (item.Kind == ChannelInboxItemKind.Message)
        {
            HandleChannelMessageNotification(
                JsonSerializer.SerializeToElement(item.Message, ChannelProtocol.SerializerOptions));
        }
        else
        {
            HandleChannelCancelNotification(
                JsonSerializer.SerializeToElement(item.Cancel, ChannelProtocol.SerializerOptions));
        }
    }

    private async Task StopPumpAsync()
    {
        if (_pumpCts is null)
        {
            return;
        }

        await _pumpCts.CancelAsync();
        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _pumpCts.Dispose();
        _pumpCts = null;
        _pumpTask = null;
    }

    public void HandleChannelMessageNotification(JsonElement payload)
    {
        ChannelMessageNotification? notification;
        try
        {
            notification = ChannelProtocol.Deserialize<ChannelMessageNotification>(payload);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Discarding malformed channel/message notification on {ChannelId}", ChannelId);
            return;
        }

        if (notification is null)
        {
            return;
        }

        var message = new ChannelMessage
        {
            ConversationId = notification.ConversationId,
            Content = notification.Content,
            Sender = notification.Sender,
            ChannelId = ChannelId,
            AgentId = notification.AgentId,
            ReplyTo = notification.ReplyTo,
            Origin = notification.Origin,
            Location = notification.Location,
            SatelliteId = notification.SatelliteId,
            DismissedAlert = notification.DismissedAlert
        };

        _messageChannel.Writer.TryWrite(message);
    }

    public void HandleChannelCancelNotification(JsonElement payload)
    {
        ChannelCancelNotification? notification;
        try
        {
            notification = ChannelProtocol.Deserialize<ChannelCancelNotification>(payload);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Discarding malformed channel/cancel notification on {ChannelId}", ChannelId);
            return;
        }

        if (notification is null)
        {
            return;
        }

        var message = new ChannelMessage
        {
            ConversationId = notification.ConversationId,
            Content = CancelCommandContent,
            Sender = ChannelProtocol.SystemSender,
            ChannelId = ChannelId,
            AgentId = notification.AgentId
        };

        _messageChannel.Writer.TryWrite(message);
    }

    public async Task SendReplyAsync(
        string conversationId,
        string content,
        ReplyContentType contentType,
        bool isComplete,
        string? messageId,
        CancellationToken ct)
    {
        EnsureConnected();
        // send_reply fires once per streamed content chunk (hundreds per response). Building
        // the args dictionary directly avoids ChannelProtocol.ToArguments's reflection
        // SerializeToDocument + per-property Clone on the hot path; the wire JSON is
        // identical (same camelCase keys, ContentType.ToString() matches the
        // JsonStringEnumConverter output).
        await _client!.CallToolAsync(
            ChannelProtocol.SendReplyTool,
            new Dictionary<string, object?>
            {
                ["conversationId"] = conversationId,
                ["content"] = content,
                ["contentType"] = contentType.ToString(),
                ["isComplete"] = isComplete,
                ["messageId"] = messageId
            },
            cancellationToken: ct);
    }

    public async Task<ToolApprovalResult> RequestApprovalAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken ct)
    {
        EnsureConnected();
        var result = await _client!.CallToolAsync(
            ChannelProtocol.RequestApprovalTool,
            ChannelProtocol.ToArguments(new RequestApprovalParams
            {
                ConversationId = conversationId,
                Mode = ApprovalMode.Request,
                Requests = requests
            }),
            cancellationToken: ct);

        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        return Enum.TryParse<ToolApprovalResult>(text, ignoreCase: true, out var parsed)
            ? parsed
            : ToolApprovalResult.Rejected;
    }

    public async Task NotifyAutoApprovedAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken ct)
    {
        EnsureConnected();
        await _client!.CallToolAsync(
            ChannelProtocol.RequestApprovalTool,
            ChannelProtocol.ToArguments(new RequestApprovalParams
            {
                ConversationId = conversationId,
                Mode = ApprovalMode.Notify,
                Requests = requests
            }),
            cancellationToken: ct);
    }

    public async Task<string?> CreateConversationAsync(
        string agentId,
        string topicName,
        string sender,
        string? initialPrompt,
        string? address,
        string? existingConversationId,
        CancellationToken ct)
    {
        if (_client is null)
        {
            return null;
        }

        try
        {
            var tools = await _client.ListToolsAsync(cancellationToken: ct);
            if (tools.All(t => t.Name != ChannelProtocol.CreateConversationTool))
            {
                return null;
            }

            var result = await _client.CallToolAsync(
                ChannelProtocol.CreateConversationTool,
                new Dictionary<string, object?>
                {
                    ["agentId"] = agentId,
                    ["topicName"] = topicName,
                    ["sender"] = sender,
                    ["initialPrompt"] = initialPrompt,
                    ["address"] = address,
                    ["existingConversationId"] = existingConversationId
                },
                cancellationToken: ct);

            // A rejected create (e.g. unknown voice satellite) comes back as IsError with the
            // error text as content; treat it as "no conversation" rather than a conversation id.
            if (result.IsError == true)
            {
                return null;
            }

            return result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        }
        catch (McpException)
        {
            return null;
        }
    }

    public async Task RegisterAgentsAsync(IReadOnlyList<AgentCatalogEntry> agents, CancellationToken ct)
    {
        if (_client is null)
        {
            return;
        }

        var tools = await _client.ListToolsAsync(cancellationToken: ct);
        if (tools.All(t => t.Name != ChannelProtocol.RegisterAgentsTool))
        {
            return;
        }

        await _client.CallToolAsync(
            ChannelProtocol.RegisterAgentsTool,
            ChannelProtocol.ToArguments(new RegisterAgentsParams { Agents = agents }),
            cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        await StopPumpAsync();
        _messageChannel.Writer.TryComplete();
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        if (_client is null)
        {
            return false;
        }

        try
        {
            await _client.ListToolsAsync(cancellationToken: ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task ReconnectAsync(string endpoint, CancellationToken ct)
    {
        await StopPumpAsync();
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        await ConnectAsync(endpoint, ct);
    }

    private void EnsureConnected()
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        }
    }
}