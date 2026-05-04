using System.Text.Json;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Clients.Channels;

public sealed class McpChannelConnection(string channelId) : IChannelConnection, IMcpChannelConnection, IAsyncDisposable
{
    private const string ChannelMessageNotification = "notifications/channel/message";

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
            ChannelMessageNotification,
            (notification, _) =>
            {
                if (notification.Params is { } paramsNode)
                {
                    var element = JsonSerializer.Deserialize<JsonElement>(paramsNode.ToJsonString());
                    HandleChannelMessageNotification(element);
                }

                return ValueTask.CompletedTask;
            });
    }

    public void HandleChannelMessageNotification(JsonElement payload)
    {
        var conversationId = payload.GetProperty("conversationId").GetString()!;
        var content = payload.GetProperty("content").GetString()!;
        var sender = payload.GetProperty("sender").GetString()!;
        var agentId = payload.TryGetProperty("agentId", out var agentIdProp)
            ? agentIdProp.GetString()
            : null;

        var message = new ChannelMessage
        {
            ConversationId = conversationId,
            Content = content,
            Sender = sender,
            ChannelId = ChannelId,
            AgentId = agentId
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
        await _client!.CallToolAsync(
            "send_reply",
            new Dictionary<string, object?>
            {
                ["conversationId"] = conversationId,
                ["content"] = content,
                ["contentType"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(contentType.ToString()),
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
            "request_approval",
            new Dictionary<string, object?>
            {
                ["conversationId"] = conversationId,
                ["mode"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(nameof(ApprovalMode.Request)),
                ["requests"] = JsonSerializer.Serialize(requests)
            },
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
            "request_approval",
            new Dictionary<string, object?>
            {
                ["conversationId"] = conversationId,
                ["mode"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(nameof(ApprovalMode.Notify)),
                ["requests"] = JsonSerializer.Serialize(requests)
            },
            cancellationToken: ct);
    }

    public async Task<string?> CreateConversationAsync(
        string agentId,
        string topicName,
        string sender,
        CancellationToken ct)
    {
        if (_client is null)
        {
            return null;
        }

        try
        {
            var tools = await _client.ListToolsAsync(cancellationToken: ct);
            if (tools.All(t => t.Name != "create_conversation"))
            {
                return null;
            }

            var result = await _client.CallToolAsync(
                "create_conversation",
                new Dictionary<string, object?>
                {
                    ["agentId"] = agentId,
                    ["topicName"] = topicName,
                    ["sender"] = sender
                },
                cancellationToken: ct);

            return result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        }
        catch (McpException)
        {
            return null;
        }
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