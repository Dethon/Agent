using System.Text.Json;
using McpServerScheduling.McpResources;
using Shouldly;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class FileSystemResourceTests
{
    [Fact]
    public void GetInfo_PublishesSchedulesMountMetadata()
    {
        var json = new FileSystemResource().GetInfo();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("name").GetString().ShouldBe("schedules");
        doc.RootElement.GetProperty("mountPoint").GetString().ShouldBe("/schedules");
        var description = doc.RootElement.GetProperty("description").GetString()!;
        description.ShouldContain("schedule.json");
        description.ShouldContain("cron");
        description.ShouldContain("runAt");
        description.ShouldContain("UTC");
        description.ShouldContain("status.json");
        description.ShouldContain("run_now.sh");
        description.ShouldContain("/schedules/");
    }

    // The resource blurb and the scheduling prompt are two copies of the same idiom, reached by
    // different reads. If only one of them says who owns a new schedule, ownership depends on
    // which surface the agent happened to consult -- so the default-owner rule lives on both.
    [Fact]
    public void GetInfo_CarriesTheSameDefaultOwnerRuleAsThePrompt()
    {
        var json = new FileSystemResource().GetInfo();
        using var doc = JsonDocument.Parse(json);
        var description = doc.RootElement.GetProperty("description").GetString()!;

        description.ShouldContain("schedule against yourself");
        description.ShouldContain("unless the user names another agent");
    }
}