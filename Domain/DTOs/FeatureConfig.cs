using Domain.Agents;
using Domain.DTOs.Channel;

namespace Domain.DTOs;

// ConversationContextProvider is a delegate rather than a plain value because the parent's
// conversation context is per-turn while a FeatureConfig -- and every tool built from it -- is
// per-agent, and an agent instance serves every turn of a conversation activation. A captured
// value would therefore go stale (and be wrong outright when a later turn resolves a different
// delivery target). The delegate is invoked at tool-run time, inside the parent's own tool
// invocation, so what it returns is the context of the very turn that spawned the subagent.
public record FeatureConfig(
    IReadOnlySet<string>? EnabledTools = null,
    Func<SubAgentDefinition, DisposableAgent>? SubAgentFactory = null,
    string? UserId = null,
    Func<ConversationContext?>? ConversationContextProvider = null);