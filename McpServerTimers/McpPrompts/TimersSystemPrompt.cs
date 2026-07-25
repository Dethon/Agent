using System.ComponentModel;
using Domain.Contracts;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerTimers.McpPrompts;

[McpServerPromptType]
public class TimersSystemPrompt(ISatelliteCatalog satellites, TimeProvider time)
{
    // MCP prompts are fetched while the agent session is built, so the roster fetch must never
    // hang on a sick hub: it gets a cap far below the named client's 15s timeout, and any failure
    // fails open to the roster-less static text (create-time errors still name the roster, so the
    // agent can recover in-conversation). Only the caller's own cancellation propagates.
    private static readonly TimeSpan _rosterTimeout = TimeSpan.FromSeconds(2);

    [McpServerPrompt(Name = TimerPrompt.Name)]
    [Description(TimerPrompt.Description)]
    public async Task<string> GetTimerPrompt(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_rosterTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            return TimerPrompt.Build(await satellites.GetAllAsync(linked.Token));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return TimerPrompt.Build([]);
        }
    }
}