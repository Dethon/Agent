using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.Extensions;
using Domain.Prompts;
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public sealed class McpAgent : DisposableAgent
{
    private readonly string? _customInstructions;
    private readonly string _description;
    private readonly IReadOnlyList<AIFunction> _domainTools;
    private readonly string[] _endpoints;
    private readonly ChatClientAgent _innerAgent;
    private readonly string _name;
    private readonly string _userId;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private readonly ConcurrentDictionary<AgentSession, ThreadSession> _threadSessions = [];
    private int _isDisposed;

    public override string? Name => _innerAgent.Name;
    public override string? Description => _innerAgent.Description;

    public McpAgent(
        string[] endpoints,
        IChatClient chatClient,
        string name,
        string description,
        IThreadStateStore stateStore,
        string userId,
        string? customInstructions = null,
        IReadOnlyList<AIFunction>? domainTools = null)
    {
        _endpoints = endpoints;
        _name = name;
        _description = description;
        _userId = userId;
        _customInstructions = customInstructions;
        _domainTools = domainTools ?? [];
        _innerAgent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    // OpenRouter expects a JSON object; using JsonObject avoids anonymous-type serialization quirks.
                    ["reasoning"] = new JsonObject { ["effort"] = "low" },
                    ["include_reasoning"] = true,
                    ["reasoning_effort"] = "low"
                }
            },
            Description = description,
            ChatHistoryProviderFactory = (ctx, ct) => RedisChatMessageStore.Create(stateStore, ctx, ct)
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

    public override ValueTask<AgentSession> GetNewSessionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        return _innerAgent.GetNewSessionAsync(cancellationToken);
    }

    public override async ValueTask DisposeThreadSessionAsync(AgentSession thread)
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

    public override ValueTask<AgentSession> DeserializeSessionAsync(
        JsonElement serializedThread,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        if (serializedThread.TryGetProperty("StoreState", StringComparison.InvariantCultureIgnoreCase, out _))
        {
            return _innerAgent.DeserializeSessionAsync(serializedThread, jsonSerializerOptions, cancellationToken);
        }

        var json = new JsonObject
        {
            ["StoreState"] = serializedThread.ToJsonNode()
        };
        serializedThread = JsonSerializer.Deserialize<JsonElement>(json.ToJsonString());
        return _innerAgent.DeserializeSessionAsync(serializedThread, jsonSerializerOptions, cancellationToken);
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        var response = RunCoreStreamingAsync(messages, thread, options, cancellationToken);
        return (await response.ToArrayAsync(cancellationToken)).ToAgentResponse();
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        thread ??= await GetNewSessionAsync(cancellationToken);
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

    private async IAsyncEnumerable<AgentResponseUpdate> RunStreamingCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession thread,
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

    private ChatClientAgentRunOptions CreateRunOptions(ThreadSession session)
    {
        var timeContext = $"Current time: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC";

        var prompts = session.ClientManager.Prompts
            .Prepend(BasePrompt.Instructions);

        if (!string.IsNullOrEmpty(_customInstructions))
        {
            prompts = prompts.Prepend(_customInstructions);
        }

        prompts = prompts.Prepend(timeContext);

        return new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [.. session.Tools],
            Instructions = string.Join("\n\n", prompts)
        });
    }

    private async Task<ThreadSession> GetOrCreateSessionAsync(AgentSession thread, CancellationToken ct)
    {
        return await _syncLock.WithLockAsync(async () =>
        {
            if (_threadSessions.TryGetValue(thread, out var existing))
            {
                return existing;
            }

            var newSession = await ThreadSession
                .CreateAsync(_endpoints, _name, _userId, _description, _innerAgent, thread, _domainTools, ct);
            _threadSessions[thread] = newSession;
            return newSession;
        }, ct);
    }
}