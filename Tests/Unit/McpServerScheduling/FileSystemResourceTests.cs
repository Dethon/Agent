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
        doc.RootElement.GetProperty("description").GetString()!.ShouldContain("schedule.json");
    }
}