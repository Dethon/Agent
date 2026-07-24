using System.Text.Json;
using McpServerTimers.McpResources;
using Shouldly;

namespace Tests.Unit.McpServerTimers;

public class FileSystemResourceTests
{
    [Fact]
    public void GetInfo_PublishesTimersMountMetadata()
    {
        var json = new FileSystemResource().GetInfo();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("name").GetString().ShouldBe("timers");
        doc.RootElement.GetProperty("mountPoint").GetString().ShouldBe("/timers");
        var description = doc.RootElement.GetProperty("description").GetString()!;
        description.ShouldContain("timer.json");
        description.ShouldContain("status.json");
        description.ShouldContain("dismiss.sh");
        description.ShouldContain("durationSeconds");
        description.ShouldContain("target");
    }
}