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
}