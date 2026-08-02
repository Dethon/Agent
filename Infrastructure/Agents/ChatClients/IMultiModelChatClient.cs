using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

// A chat client that can run a different model per request (per-message config patches).
// EffectiveModel is what the last request actually ran: the resolved override when one
// applied, else the configured model. Consumers stamping models on metrics read this
// instead of re-resolving the patch whitelist themselves.
public interface IMultiModelChatClient : IChatClient
{
    string EffectiveModel { get; }
}