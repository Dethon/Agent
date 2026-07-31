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

    // The blurb is a second copy of the timing contract, and it contradicted the engine on both
    // halves: ComputeNextRunAt evaluates cron against TimeProvider.LocalTimeZone, not UTC, and ToUtc
    // reads a zoneless runAt as wall clock in that same zone rather than rejecting it. An agent that
    // reads this surface instead of the prompt would stamp UTC on both and fire hours off.
    [Fact]
    public void GetInfo_TimingContract_MatchesTheEngineInsteadOfClaimingUtc()
    {
        var json = new FileSystemResource().GetInfo();
        using var doc = JsonDocument.Parse(json);
        var description = doc.RootElement.GetProperty("description").GetString()!;

        description.ShouldNotContain("UTC cron");
        description.ShouldNotContain("MUST include a time zone");
        description.ShouldContain(TimeZoneInfo.Local.Id);
    }
}