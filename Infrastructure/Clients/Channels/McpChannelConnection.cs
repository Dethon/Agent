using System.Text.Json;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Clients.Channels;

public sealed class McpChannelConnection(string channelId, ILogger<McpChannelConnection>? logger = null)
    : IChannelConnection, IMcpChannelConnection, IAsyncDisposable
{
    private const string CancelCommandContent = "/cancel";
    private const string SystemSender = "system";

    private readonly Channel<ChannelMessage> _messageChannel = Channel.CreateUnbounded<ChannelMessage>();
    private McpClient? _client;

    public string ChannelId { get; } = channelId;

    public IAsyncEnumerable<ChannelMessage> Messages => _messageChannel.Reader.ReadAllAsync();

    public async Task ConnectAsync(string endpoint, CancellationToken ct)
    {
        _client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(endpoint) }),
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = $"channel-{ChannelId}",
                    Version = "1.0.0"
                }
            },
            cancellationToken: ct);

        _client.RegisterNotificationHandler(
            ChannelProtocol.MessageNotification,
            (notification, _) =>
            {
                if (notification.Params is { } paramsNode)
                {
                    var element = JsonSerializer.Deserialize<JsonElement>(paramsNode.ToJsonString());
                    HandleChannelMessageNotification(element);
                }

                return ValueTask.CompletedTask;
            });

        _client.RegisterNotificationHandler(
            ChannelProtocol.CancelNotification,
            (notification, _) =>
            {
                if (notification.Params is { } paramsNode)
                {
                    var element = JsonSerializer.Deserialize<JsonElement>(paramsNode.ToJsonString());
                    HandleChannelCancelNotification(element);
                }

                return ValueTask.CompletedTask;
            });
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
            Origin = notification.Origin
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
            Sender = SystemSender,
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
                ChannelProtocol.ToArguments(new CreateConversationParams
                {
                    AgentId = agentId,
                    TopicName = topicName,
                    Sender = sender,
                    InitialPrompt = initialPrompt
                }),
                cancellationToken: ct);

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