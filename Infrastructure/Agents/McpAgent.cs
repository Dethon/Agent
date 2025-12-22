using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public sealed class McpAgent : DisposableAgent
{
    private readonly string _description;
    private readonly string[] _endpoints;
    private readonly ChatClientAgent _innerAgent;
    private readonly string _name;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private readonly ConcurrentDictionary<AgentThread, ThreadSession> _threadSessions = [];
    private int _isDisposed;

    public override string? Name => _innerAgent.Name;
    public override string? Description => _innerAgent.Description;

    public McpAgent(
        string[] endpoints,
        IChatClient chatClient,
        string name,
        string description,
        IThreadStateStore stateStore)
    {
        _endpoints = endpoints;
        _name = name;
        _description = description;
        _innerAgent = chatClient.CreateAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoning_effort"] = "low" }
            },
            Description = description,
            ChatMessageStoreFactory = ctx => RedisChatMessageStore.CreateAsync(stateStore, ctx).GetAwaiter().GetResult()
        });
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        await _syncLock.WithLockAsync(async () =>
        {
            foreach (var session in _threadSessions.Values)
            {
                await session.DisposeAsync();
            }

            _threadSessions.Clear();
        });
        _syncLock.Dispose();
    }

    public override AgentThread GetNewThread()
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        return _innerAgent.GetNewThread();
    }

    public override async ValueTask DisposeThreadSessionAsync(AgentThread thread)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        await _syncLock.WithLockAsync(async () =>
        {
            if (_threadSessions.Remove(thread, out var session))
            {
                await session.DisposeAsync();
            }
        });
    }

    public override AgentThread DeserializeThread(
        JsonElement serializedThread,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        if (!serializedThread.TryGetProperty("StoreState", StringComparison.InvariantCultureIgnoreCase, out _))
        {
            var json = new JsonObject
            {
                ["StoreState"] = JsonNode.Parse(serializedThread.ToString())
            };
            serializedThread = JsonSerializer.Deserialize<JsonElement>(json.ToJsonString());
        }

        return _innerAgent.DeserializeThread(serializedThread, jsonSerializerOptions);
    }

    public override async Task<AgentRunResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        var response = RunStreamingAsync(messages, thread, options, cancellationToken);
        return (await response.ToArrayAsync(cancellationToken)).ToAgentRunResponse();
    }

    public override async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        thread ??= GetNewThread();
        var session = await GetOrCreateSessionAsync(thread, cancellationToken);
        await session.ResourceManager.EnsureChannelActive(cancellationToken);
        options ??= CreateRunOptions(session);

        var mainResponses = RunStreamingCoreAsync(messages, thread, session, options, cancellationToken);
        var notificationResponses = session.ResourceManager.SubscriptionChannel.Reader.ReadAllAsync(cancellationToken);

        await foreach (var update in mainResponses.Merge(notificationResponses, cancellationToken))
        {
            yield return update;
        }

        // If channel was replaced during execution, drain the new one.
        // If it is the old one it will be completed already
        await foreach (var update in session.ResourceManager.SubscriptionChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return update;
        }
    }

    private async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread thread,
        ThreadSession session,
        AgentRunOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        await foreach (var update in _innerAgent.RunStreamingAsync(messages, thread, options, ct))
        {
            yield return update;
        }

        await session.ResourceManager.SyncResourcesAsync(session.ClientManager.Clients, ct);
    }

    private static ChatClientAgentRunOptions CreateRunOptions(ThreadSession session)
    {
        return new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [.. session.ClientManager.Tools],
            Instructions = string.Join("\n\n", session.ClientManager.Prompts)
        });
    }

    private async Task<ThreadSession> GetOrCreateSessionAsync(AgentThread thread, CancellationToken ct)
    {
        return await _syncLock.WithLockAsync(async () =>
        {
            if (_threadSessions.TryGetValue(thread, out var existing))
            {
                return existing;
            }

            var newSession = await ThreadSession.CreateAsync(_endpoints, _name, _description, _innerAgent, thread, ct);
            _threadSessions[thread] = newSession;
            return newSession;
        }, ct);
    }
}