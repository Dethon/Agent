using Domain.DTOs;
using Domain.DTOs.FileSystem;
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
        var result = await fs.GlobAsync("/", "*", CancellationToken.None);
        var glob = result.ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        glob.Entries.ShouldContain("/jonas");
        glob.Entries.ShouldContain("/jack");
    }

    [Fact]
    public async Task Glob_AgentDir_ListsItsSchedules()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var result = await fs.GlobAsync("/jonas", "*", CancellationToken.None);
        var glob = result.ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        glob.Entries.ShouldContain("/jonas/morning-news");
    }

    [Fact]
    public async Task Read_AgentInfo_ReturnsMetadata()
    {
        var fs = Build(new FakeScheduleStore());
        var result = await fs.ReadAsync("/jonas/agent_info.json", null, null, CancellationToken.None);
        var read = result.ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldContain("\"id\"");
        read.Content.ShouldContain("Jonas");
    }

    [Fact]
    public async Task Read_ScheduleFile_ReturnsSpecWithoutAgentId()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "summarize", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var result = await fs.ReadAsync("/jonas/morning-news/schedule.json", null, null, CancellationToken.None);
        var read = result.ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldContain("summarize");
        read.Content.ShouldNotContain("agentId");
    }

    [Fact]
    public async Task Read_UnknownAgent_ReturnsNotFoundEnvelope()
    {
        var fs = Build(new FakeScheduleStore());
        var result = await fs.ReadAsync("/ghost/x/schedule.json", null, null, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsReadResult>.Err>();
    }

    [Fact]
    public async Task Info_ExistingAgentDir_ReportsDirectory()
    {
        var fs = Build(new FakeScheduleStore());
        var result = await fs.InfoAsync("/jonas", CancellationToken.None);
        var info = result.ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        info.Exists.ShouldBeTrue();
        info.IsDirectory.ShouldBe(true);
    }

    [Fact]
    public async Task Info_UnknownAgent_ReportsNotExisting()
    {
        var fs = Build(new FakeScheduleStore());
        var result = await fs.InfoAsync("/ghost", CancellationToken.None);
        var info = result.ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        info.Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task Search_MatchesByPromptIdOrAgent()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "summarize the news", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var result = await fs.SearchAsync("news", CancellationToken.None);

        var search = result.ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.Results.Count.ShouldBe(1);
        search.Results[0].File.ShouldBe("/jonas/morning-news/schedule.json");
        search.TotalMatches.ShouldBe(1);
    }
}