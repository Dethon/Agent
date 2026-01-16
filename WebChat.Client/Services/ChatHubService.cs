using System.Net.Http.Json;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Models;

namespace WebChat.Client.Services;

public sealed class ChatHubService : IAsyncDisposable
{
    private HubConnection? _hubConnection;
    private readonly HttpClient _httpClient;

    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;
    public StoredTopic? CurrentTopic { get; private set; }

    public event Action? OnStateChanged;
    public event Func<Task>? OnReconnected;

    public ChatHubService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task ConnectAsync()
    {
        if (_hubConnection is not null)
        {
            return;
        }

        var config = await _httpClient.GetFromJsonAsync<AppConfig>("/api/config");
        var agentUrl = config?.AgentUrl ?? "http://localhost:5000";
        var hubUrl = $"{agentUrl.TrimEnd('/')}/hubs/chat";

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.Closed += _ =>
        {
            OnStateChanged?.Invoke();
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += async _ =>
        {
            if (OnReconnected is not null)
            {
                await OnReconnected.Invoke();
            }

            OnStateChanged?.Invoke();
        };

        await _hubConnection.StartAsync();
        OnStateChanged?.Invoke();
    }

    public async Task<IReadOnlyList<AgentInfo>> GetAgentsAsync()
    {
        if (_hubConnection is null)
        {
            return [];
        }

        return await _hubConnection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");
    }

    public async Task<bool> StartSessionAsync(StoredTopic topic)
    {
        if (_hubConnection is null)
        {
            return false;
        }

        var success =
            await _hubConnection.InvokeAsync<bool>("StartSession", topic.AgentId, topic.TopicId, topic.ChatId,
                topic.ThreadId);
        if (success)
        {
            CurrentTopic = topic;
            OnStateChanged?.Invoke();
        }

        return success;
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(long chatId, long threadId)
    {
        if (_hubConnection is null)
        {
            return [];
        }

        return await _hubConnection.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>("GetHistory", chatId, threadId);
    }

    public async Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync()
    {
        if (_hubConnection is null)
        {
            return [];
        }

        return await _hubConnection.InvokeAsync<IReadOnlyList<TopicMetadata>>("GetAllTopics");
    }

    public async Task SaveTopicAsync(TopicMetadata topic)
    {
        if (_hubConnection is null)
        {
            return;
        }

        await _hubConnection.InvokeAsync("SaveTopic", topic);
    }

    public async IAsyncEnumerable<ChatStreamMessage> SendMessageAsync(string message)
    {
        if (_hubConnection is null || CurrentTopic is null)
        {
            yield break;
        }

        var stream = _hubConnection.StreamAsync<ChatStreamMessage>("SendMessage", CurrentTopic.TopicId, message);

        await foreach (var item in stream)
        {
            yield return item;
        }
    }

    public async Task<StreamState?> GetStreamStateAsync(string topicId)
    {
        if (_hubConnection is null)
        {
            return null;
        }

        return await _hubConnection.InvokeAsync<StreamState?>("GetStreamState", topicId);
    }

    public async IAsyncEnumerable<ChatStreamMessage> ResumeStreamAsync(string topicId)
    {
        if (_hubConnection is null)
        {
            yield break;
        }

        var stream = _hubConnection.StreamAsync<ChatStreamMessage>("ResumeStream", topicId);

        await foreach (var item in stream)
        {
            yield return item;
        }
    }

    public async Task<bool> IsProcessingAsync(string topicId)
    {
        if (_hubConnection is null)
        {
            return false;
        }

        return await _hubConnection.InvokeAsync<bool>("IsProcessing", topicId);
    }

    public async Task CancelAsync()
    {
        if (_hubConnection is null || CurrentTopic is null)
        {
            return;
        }

        await _hubConnection.InvokeAsync("CancelTopic", CurrentTopic.TopicId);
    }

    public async Task DeleteTopicAsync(StoredTopic topic)
    {
        if (_hubConnection is null)
        {
            return;
        }

        await _hubConnection.InvokeAsync("DeleteTopic", topic.TopicId, topic.ChatId, topic.ThreadId);

        if (CurrentTopic?.TopicId == topic.TopicId)
        {
            CurrentTopic = null;
            OnStateChanged?.Invoke();
        }
    }

    public void ClearCurrentTopic()
    {
        CurrentTopic = null;
        OnStateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
        }
    }

    public static string GenerateTopicId()
    {
        return Guid.NewGuid().ToString("N");
    }

    /// <summary>
    ///     Derives a deterministic ChatId from a TopicId.
    ///     Each topic gets its own unique ChatId for proper message routing.
    /// </summary>
    public static long GetChatIdForTopic(string topicId)
    {
        return GetDeterministicHash(topicId, seed: 0x1234);
    }

    /// <summary>
    ///     Derives a deterministic ThreadId from a TopicId.
    ///     Used for conversation state persistence.
    ///     Returns a value that fits in int (for ChatPrompt compatibility).
    /// </summary>
    public static long GetThreadIdForTopic(string topicId)
    {
        return GetDeterministicHash(topicId, seed: 0x5678) & 0x7FFFFFFF; // Fit in positive int
    }

    private static long GetDeterministicHash(string input, long seed)
    {
        // FNV-1a hash algorithm - deterministic across sessions
        const long fnvPrime = 0x100000001b3;
        var hash = unchecked((long)0xcbf29ce484222325) ^ seed;

        foreach (var c in input)
        {
            hash ^= c;
            hash = unchecked(hash * fnvPrime);
        }

        return hash & 0x7FFFFFFFFFFFFFFF; // Ensure positive
    }
}

internal record AppConfig(string? AgentUrl);