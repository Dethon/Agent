# Per-Message Agent Config Patch

Date: 2026-08-01
Status: approved

## Goal

Let a channel attach an optional config patch to each message it sends to the agent. The patch overrides parts of the agent's configuration for that turn only. Different clients in the same conversation can use different patches without interfering. For now only the SignalR (WebChat) channel uses it, and only `model` and `reasoningEffort` are patchable. Adding a new patchable field must be cheap.

WebChat gets a settings UI where the user picks a model (from a whitelist) and a reasoning effort (all supported values) per agent. Choices persist in browser local storage. Initial values match the backend's configured defaults for that agent.

## Non-goals

- Patching subagent runs. Subagents keep their configured model and effort.
- Patching from Telegram, voice, ServiceBus, or scheduling channels.
- Patching any field beyond model and reasoning effort (the mechanism must allow it later, but nothing else ships now).

## 1. Protocol and agent-side application

New Domain record:

```csharp
public record AgentConfigPatch
{
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
}
```

A null field means "no override". A future patchable field is one new optional property.

- `ChannelMessageNotification` and `ChannelMessage` get an optional `ConfigPatch` property. It is absent on the wire when unused, following the precedent of `Location` and `SatelliteId`.
- `McpChannelConnection` maps the field from notification to `ChannelMessage`.
- `ChatMonitor.BuildUserMessageAsync` stamps the patch onto the `ChatMessage` with a new `SetConfigPatch` annotation extension, next to `SetLocation` and friends.
- Reasoning effort: `McpAgent.CreateRunOptions` reads the patch from the turn's user message. A patched effort goes through the existing `ParseEffort`; an invalid value falls back to the configured default.
- Model: `OpenRouterChatClient` reads the patch annotation and `OpenRouterHttpHelpers.PrepareRequestBodyAsync` writes the override into the request body (`obj["model"]`), the same hook that stamps `session_id`. A model not on the whitelist is ignored and the configured model is used.
- Subagent calls never see the patch.

## 2. Whitelist and defaults distribution

- Agent `appsettings.json` gets a `patchableModels` list, shared by all agents:
  - `{ "id": "openai/gpt-5.6-luna", "name": "GPT Luna" }`
  - `{ "id": "z-ai/glm-5.2", "name": "GLM 5.2" }`
- Patchable reasoning efforts are all values `ParseEffort` accepts: none, low, medium, high, xhigh, max. They are not configured; they come from code.
- `AgentCatalogEntry` is widened with `DefaultModel`, `DefaultReasoningEffort`, `PatchableModels` (id + display name), and `PatchableReasoningEfforts`.
- The widened catalog flows through the existing `register_agents` -> hub `GetAgents` / `OnAgentsUpdated` path. WebChat learns defaults and choices with no new endpoint, and they refresh on reconnect.

## 3. WebChat UI and persistence

- New store slice (`AgentSettings*`) in the Blazor client following the existing action/reducer/effect pattern.
- A settings control next to `AgentSelector` with two dropdowns: model (display names from `PatchableModels`) and reasoning effort. It always edits the currently selected agent's settings.
- Initial values are the selected agent's `DefaultModel` and `DefaultReasoningEffort` from the catalog.
- Choices persist through `ILocalStorageService` under `agentConfigPatch:{agentId}`. On load, a stored value that is no longer whitelisted is discarded in favor of the default.
- On send, the client includes only the fields that differ from the agent's defaults. If nothing differs, it sends no patch, so default traffic is unchanged.
- `SendMessage` and `EnqueueMessage` hub methods gain an optional patch argument. The SignalR `ChannelNotificationEmitter` puts it on the `ChannelMessageNotification`. Emitters of other channels are separate classes and stay untouched.

## 4. Error handling

- Non-whitelisted or unknown model in a patch: ignored, configured model used.
- Invalid reasoning effort: `ParseEffort` falls back to the configured default.
- Missing patch: behavior is byte-identical to today.
- Stale local-storage values: discarded against the current whitelist on load.

## 5. Testing

Red-Green-Refactor throughout.

- Protocol round-trip with and without `ConfigPatch` (`ChannelProtocolTests`).
- `McpChannelConnection` parsing of the new field.
- `ChatMonitor` stamping the patch onto the user message.
- `McpAgent` effort override and fallback.
- `OpenRouterHttpHelpers` model stamping and whitelist rejection.
- `AgentCatalogEntry` widening through `register_agents`.
- SignalR hub pass-through of the patch argument.
- Client-side: state slice, local-storage persistence, stale-value discard, patch-only-on-diff, using the WebChat client's existing test setup.

## Notes

- Per-message model switching changes the OpenRouter prompt-cache key for that turn. Caches are per model, so switching back to the previous model recovers its cache key (subject to the provider's cache TTL). This is accepted.
- The `openrouter-routing.md` rule's wholesale-override and `session_id` cache-pinning constraints apply to the `PrepareRequestBodyAsync` change.
