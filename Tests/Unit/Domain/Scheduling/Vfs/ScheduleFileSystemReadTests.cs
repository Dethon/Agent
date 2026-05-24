using Domain.DTOs;
using Domain.Tools;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Validation;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public class ScheduleFileSystemReadTests
{
    private static ScheduleFileSystem Build(FakeScheduleStore store)
    {
        var catalog = new FakeAgentCatalog([
            new ScheduleAgentInfo("jonas", "Jonas", "general"),
            new ScheduleAgentInfo("jack", "Jack", "library")
        ]);
        return new ScheduleFileSystem(store, catalog, new CronValidator());
    }

    [Fact]
    public async Task Glob_Root_ListsAllAgentsIncludingEmpty()
    {
        var fs = Build(new FakeScheduleStore());
        var node = await fs.GlobAsync("/", "*", CancellationToken.None);
        var entries = node["entries"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        entries.ShouldContain("/jonas");
        entries.ShouldContain("/jack");
    }

    [Fact]
    public async Task Glob_AgentDir_ListsItsSchedules()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var node = await fs.GlobAsync("/jonas", "*", CancellationToken.None);
        var entries = node["entries"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        entries.ShouldContain("/jonas/morning-news");
    }

    [Fact]
    public async Task Read_AgentInfo_ReturnsMetadata()
    {
        var fs = Build(new FakeScheduleStore());
        var node = await fs.ReadAsync("/jonas/agent_info.json", null, null, CancellationToken.None);
        node["content"]!.GetValue<string>().ShouldContain("\"id\"");
        node["content"]!.GetValue<string>().ShouldContain("Jonas");
    }

    [Fact]
    public async Task Read_ScheduleFile_ReturnsSpecWithoutAgentId()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "summarize", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var node = await fs.ReadAsync("/jonas/morning-news/schedule.json", null, null, CancellationToken.None);
        var content = node["content"]!.GetValue<string>();
        content.ShouldContain("summarize");
        content.ShouldNotContain("agentId");
    }

    [Fact]
    public async Task Read_UnknownAgent_ReturnsNotFoundEnvelope()
    {
        var fs = Build(new FakeScheduleStore());
        var node = await fs.ReadAsync("/ghost/x/schedule.json", null, null, CancellationToken.None);
        ToolErrorResult.IsErrorEnvelope(node).ShouldBeTrue();
    }

    [Fact]
    public async Task Info_ExistingAgentDir_ReportsDirectory()
    {
        var fs = Build(new FakeScheduleStore());
        var node = await fs.InfoAsync("/jonas", CancellationToken.None);
        node["exists"]!.GetValue<bool>().ShouldBeTrue();
        node["isDirectory"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Info_UnknownAgent_ReportsNotExisting()
    {
        var fs = Build(new FakeScheduleStore());
        var node = await fs.InfoAsync("/ghost", CancellationToken.None);
        node["exists"]!.GetValue<bool>().ShouldBeFalse();
    }
}