using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Validation;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public class ScheduleFileSystemSearchTests
{
    private static ScheduleFileSystem Build(FakeScheduleStore store) =>
        new(store, new FakeAgentCatalog([
            new ScheduleAgentInfo("jonas", "Jonas", "general"),
            new ScheduleAgentInfo("jack", "Jack", "library")
        ]), new CronValidator());

    private static async Task<FakeScheduleStore> Seed()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "summarize the news", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        await store.CreateAsync(new Schedule { Id = "weather", AgentId = "jack", Prompt = "the weather report", CronExpression = "0 7 * * *", CreatedAt = DateTime.UtcNow });
        return store;
    }

    private static Task<FsResult<FsSearchResult>> Search(ScheduleFileSystem fs, string query,
        bool regex = false, string? path = null, string? directoryPath = null, string? filePattern = null,
        int maxResults = 50, int contextLines = 1, VfsTextSearchOutputMode outputMode = VfsTextSearchOutputMode.Content)
        => fs.SearchAsync(query, regex, path, directoryPath, filePattern, maxResults, contextLines, outputMode, CancellationToken.None);

    [Fact]
    public async Task Search_ContentMode_ReturnsLineMatches()
    {
        var fs = Build(await Seed());
        var search = (await Search(fs, "news")).ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;

        var file = search.Results.ShouldHaveSingleItem();
        file.File.ShouldBe("/jonas/morning-news/schedule.json");
        var match = file.Matches.ShouldNotBeNull().ShouldHaveSingleItem();
        match.Text.ShouldContain("summarize the news");
        match.Line.ShouldBeGreaterThan(0);
        file.MatchCount.ShouldBeNull();
    }

    [Fact]
    public async Task Search_FilesOnlyMode_ReturnsMatchCountWithoutMatches()
    {
        var fs = Build(await Seed());
        var search = (await Search(fs, "news", outputMode: VfsTextSearchOutputMode.FilesOnly))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        var file = search.Results.ShouldHaveSingleItem();
        file.MatchCount.ShouldBe(1);
        file.Matches.ShouldBeNull();
    }

    [Fact]
    public async Task Search_EchoesRegexFlagQueryAndScope()
    {
        var fs = Build(await Seed());
        var search = (await Search(fs, "news", regex: true, directoryPath: "/jonas"))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.Regex.ShouldBeTrue();
        search.Query.ShouldBe("news");
        search.Path.ShouldBe("/jonas");
    }

    [Fact]
    public async Task Search_DirectoryPath_ScopesToAgent()
    {
        var fs = Build(await Seed());
        // "the" appears in both prompts; scoping to /jack must search only jack's schedule.
        var search = (await Search(fs, "the", directoryPath: "/jack"))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.FilesSearched.ShouldBe(1);
        search.Results.ShouldHaveSingleItem().File.ShouldBe("/jack/weather/schedule.json");
    }

    [Fact]
    public async Task Search_Regex_Honored()
    {
        var fs = Build(await Seed());
        var search = (await Search(fs, "news|weather", regex: true))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.Results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Search_RespectsMaxResults_SetsTruncated()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "s", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);
        // schedule.json renders runAt/userId/deliverTo as null lines; cap matches at 1.
        var search = (await Search(fs, "null", maxResults: 1)).ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.TotalMatches.ShouldBe(1);
        search.Truncated.ShouldBeTrue();
    }
}