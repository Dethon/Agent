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
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

public sealed class McpAgent : DisposableAgent
{
    private readonly string? _customInstructions;
    private readonly string _description;
    private readonly IReadOnlyList<AIFunction> _domainTools;
    private readonly IReadOnlyList<string> _domainPrompts;
    private readonly string[] _endpoints;
    private readonly IReadOnlySet<string> _filesystemEnabledTools;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ChatClientAgent _innerAgent;
    private readonly string _name;
    private readonly string _userId;
    private readonly bool _enableResourceSubscriptions;
    private readonly ReasoningEffort? _reasoningEffort;
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
        IReadOnlyList<AIFunction>? domainTools = null,
        IReadOnlyList<string>? domainPrompts = null,
        bool enableResourceSubscriptions = true,
        IReadOnlySet<string>? filesystemEnabledTools = null, // null treated as empty (disabled)
        ILoggerFactory? loggerFactory = null,
        string? reasoningEffort = null)
    {
        _endpoints = endpoints;
        _filesystemEnabledTools = filesystemEnabledTools ?? new HashSet<string>();
        _loggerFactory = loggerFactory;
        _name = name;
        _description = description;
        _userId = userId;
        _customInstructions = customInstructions;
        _domainTools = domainTools ?? [];
        _domainPrompts = domainPrompts ?? [];
        _enableResourceSubscriptions = enableResourceSubscriptions;
        _reasoningEffort = ParseEffort(reasoningEffort);
        _innerAgent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            Description = description,
            ChatHistoryProvider = new RedisChatMessageStore(stateStore)
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

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        return _innerAgent.CreateSessionAsync(cancellationToken);
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

    protected override async ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        return await _innerAgent.SerializeSessionAsync(session, jsonSerializerOptions, cancellationToken);
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedThread,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        // ChatClientAgentSession expects { "stateBag": { "ChatHistoryProviderState": "..." }, "conversationId": ... }
        if (serializedThread.TryGetProperty("stateBag", StringComparison.InvariantCultureIgnoreCase, out _))
        {
            return _innerAgent.DeserializeSessionAsync(serializedThread, jsonSerializerOptions, cancellationToken);
        }

        // Legacy format: plain AgentKey string or other value — wrap into stateBag
        var json = new JsonObject
        {
            ["stateBag"] = new JsonObject
            {
                [RedisChatMessageStore.StateKey] = serializedThread.ToJsonNode()
            }
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
        thread ??= await CreateSessionAsync(cancellationToken);
        var session = await GetOrCreateSessionAsync(thread, cancellationToken);

        if (session.ResourceManager is not null)
        {
            await session.ResourceManager.EnsureChannelActive(cancellationToken);
        }

        options ??= CreateRunOptions(session);

        if (session.ResourceManager is null)
        {
            await foreach (var update in _innerAgent.RunStreamingAsync(messages, thread, options, cancellationToken))
            {
                yield return update;
            }
            yield break;
        }

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

        if (session.ResourceManager is not null)
        {
            await session.ResourceManager.SyncResourcesAsync(session.ClientManager.Clients, ct);
        }
    }

    private ChatClientAgentRunOptions CreateRunOptions(ThreadSession session)
    {
        var prompts = _domainPrompts
            .Concat(session.FileSystemPrompts)
            .Concat(session.ClientManager.Prompts)
            .Prepend(BasePrompt.Instructions);

        if (!string.IsNullOrEmpty(_customInstructions))
        {
            prompts = prompts.Prepend(_customInstructions);
        }

        return new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [.. session.Tools],
            Instructions = string.Join("\n\n", prompts),
            Reasoning = _reasoningEffort is null
                ? null
                : new ReasoningOptions { Effort = _reasoningEffort.Value }
        });
    }

    internal static ReasoningEffort? ParseEffort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.ToLowerInvariant() switch
        {
            "none" => ReasoningEffort.None,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "xhigh" or "extrahigh" or "extra-high" or "max" => ReasoningEffort.ExtraHigh,
            _ => throw new ArgumentException(
                $"Unknown reasoningEffort '{value}'. Valid values: none, low, medium, high, xhigh.",
                nameof(value))
        };
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
                .CreateAsync(_endpoints, _name, _userId, _description, _innerAgent,
                             thread, _domainTools, _filesystemEnabledTools, _loggerFactory,
                             ct, _enableResourceSubscriptions);
            _threadSessions[thread] = newSession;
            return newSession;
        }, ct);
    }
}
