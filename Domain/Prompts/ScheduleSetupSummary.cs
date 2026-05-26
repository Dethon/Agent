using System.Text;
using Domain.Contracts;

namespace Domain.Prompts;

public class ScheduleSetupSummary(IAgentCatalog agents)
{
    public string Get()
    {
        var ordered = agents.GetAll().OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        if (ordered.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("## Current scheduling setup\n\n");
        sb.Append("Mounted at `/schedules` — one directory per agent. Use the paths verbatim.\n\n");

        foreach (var agent in ordered)
        {
            sb.Append("/schedules/").Append(agent.Id).Append('\n');
        }

        sb.Append("\n### Agent descriptions\n");
        foreach (var agent in ordered)
        {
            sb.Append("- `").Append(agent.Id).Append("` (").Append(agent.Name).Append(')');
            if (!string.IsNullOrWhiteSpace(agent.Description))
            {
                sb.Append(" — ").Append(agent.Description);
            }
            sb.Append('\n');
        }

        return sb.ToString();
    }
}