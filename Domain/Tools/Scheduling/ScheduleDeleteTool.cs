using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Scheduling;

public class ScheduleDeleteTool(IScheduleStore store)
{
    public const string Name = "schedule_delete";

    public const string Description = """
        Deletes a scheduled agent task by ID. Use schedule_list to find schedule IDs.
        """;

    [Description(Description)]
    public async Task<JsonNode> RunAsync(
        [Description("The schedule ID to delete (from schedule_list)")]
        string scheduleId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scheduleId))
        {
            return new JsonObject { ["error"] = "scheduleId is required" };
        }

        var existing = await store.GetAsync(scheduleId, ct);
        if (existing is null)
        {
            return new JsonObject
            {
                ["status"] = "not_found",
                ["scheduleId"] = scheduleId
            };
        }

        await store.DeleteAsync(scheduleId, ct);

        return new JsonObject
        {
            ["status"] = "deleted",
            ["scheduleId"] = scheduleId,
            ["agentName"] = existing.Agent.Name
        };
    }
}
