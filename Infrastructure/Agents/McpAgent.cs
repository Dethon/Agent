using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Extensions;
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
    private bool _isDisposed;

    public McpAgent(string[] endpoints, IChatClient chatClient, string name, string description)
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
            ChatMessageStoreFactory = ctx => ctx.SerializedState.ValueKind is JsonValueKind.Object
                ? new ConcurrentChatMessageStore(ctx.SerializedState, ctx.JsonSerializerOptions)
                : new ConcurrentChatMessageStore()
        });
    }

    public override string? Name => _innerAgent.Name;
    public override string? Description => _innerAgent.Description;

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await _syncLock.WaitAsync();
        try
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            foreach (var session in _threadSessions.Values)
            {
                await session.DisposeAsync();
            }

            _threadSessions.Clear();
        }
        finally
        {
            _syncLock.Release();
            _syncLock.Dispose();
        }
    }

    public override AgentThread GetNewThread()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return _innerAgent.GetNewThread();
    }

    public override async ValueTask DisposeThreadSessionAsync(AgentThread thread)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
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
        return _innerAgent.DeserializeThread(serializedThread, jsonSerializerOptions);
    }

    public override async Task<AgentRunResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var response = RunStreamingAsync(messages, thread, options, cancellationToken);
        return (await response.ToArrayAsync(cancellationToken)).ToAgentRunResponse();
    }

    public override async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
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
    }

    private async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread thread,
        ThreadSession session,
        AgentRunOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
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
        if (_threadSessions.TryGetValue(thread, out var session))
        {
            return session;
        }

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