using Domain.DTOs.Channel;

namespace WebChat.Client.State.AgentSettings;

public static class AgentSettingsSelectors
{
    public static AgentConfigPatch? GetConfigPatch(
        AgentSettingsState state, IReadOnlyList<AgentCatalogEntry> agents, string agentId)
    {
        var agent = agents.FirstOrDefault(a => a.Id == agentId);
        var settings = state.ByAgent.GetValueOrDefault(agentId);
        if (agent is null || settings is null)
        {
            return null;
        }

        var model = Differs(settings.Model, agent.DefaultModel) ? settings.Model : null;
        var effort = Differs(settings.ReasoningEffort, agent.DefaultReasoningEffort)
            ? settings.ReasoningEffort
            : null;

        return model is null && effort is null
            ? null
            : new AgentConfigPatch { Model = model, ReasoningEffort = effort };
    }

    public static AgentModelSettings Sanitize(AgentModelSettings settings, AgentCatalogEntry agent)
    {
        var modelValid = settings.Model is { } model &&
                         (agent.PatchableModels ?? []).Any(m =>
                             string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
        var effortValid = settings.ReasoningEffort is { } effort &&
                          (agent.PatchableReasoningEfforts ?? []).Contains(
                              effort, StringComparer.OrdinalIgnoreCase);

        return new AgentModelSettings(
            modelValid ? settings.Model : agent.DefaultModel,
            effortValid ? settings.ReasoningEffort : agent.DefaultReasoningEffort);
    }

    private static bool Differs(string? selected, string? fallback) =>
        selected is not null && !string.Equals(selected, fallback, StringComparison.OrdinalIgnoreCase);
}