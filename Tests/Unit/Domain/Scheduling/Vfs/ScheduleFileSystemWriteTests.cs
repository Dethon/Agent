using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Validation;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public class ScheduleFileSystemWriteTests
{
    private static ScheduleFileSystem Build(FakeScheduleStore store)
    {
        var catalog = new MutableAgentCatalog();
        catalog.Replace([new AgentCatalogEntry("jonas", "Jonas", "general")]);
        return new ScheduleFileSystem(store, catalog, new CronValidator());
    }

    private const string ValidSpec = """{"prompt":"summarize news","cron":"0 8 * * *"}""";

    [Fact]
    public async Task Create_PersistsScheduleWithAgentIdFromPath()
    {
        var store = new FakeScheduleStore();
        var fs = Build(store);

        var result = await fs.CreateAsync("/jonas/morning-news/schedule.json", ValidSpec, false, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        var saved = store.Items["morning-news"];
        saved.AgentId.ShouldBe("jonas");
        saved.CronExpression.ShouldBe("0 8 * * *");
        saved.NextRunAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Create_UnknownAgent_IsRejected()
    {
        var fs = Build(new FakeScheduleStore());
        var result = await fs.CreateAsync("/ghost/x/schedule.json", ValidSpec, false, true, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
    }

    [Fact]
    public async Task Create_DuplicateId_IsRejected()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var result = await fs.CreateAsync("/jonas/morning-news/schedule.json", ValidSpec, false, true, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
    }

    [Fact]
    public async Task Create_WithBothCronAndRunAt_IsRejected()
    {
        var fs = Build(new FakeScheduleStore());
        var spec = """{"prompt":"p","cron":"0 8 * * *","runAt":"2999-01-01T00:00:00Z"}""";
        var result = await fs.CreateAsync("/jonas/x/schedule.json", spec, false, true, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
    }

    [Fact]
    public async Task Edit_UpdatesSchedulePrompt()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "summarize news", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var result = await fs.EditAsync("/jonas/morning-news/schedule.json",
            [new TextEdit("summarize news", "summarize sports")], CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsEditResult>.Ok>();
        store.Items["morning-news"].Prompt.ShouldBe("summarize sports");
    }

    [Fact]
    public async Task Move_ReassignsAgent()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var catalog = new MutableAgentCatalog();
        catalog.Replace([new AgentCatalogEntry("jonas", "J", null), new AgentCatalogEntry("home", "Home", null)]);
        var fs = new ScheduleFileSystem(store, catalog, new CronValidator());

        var result = await fs.MoveAsync("/jonas/morning-news", "/home/morning-news", CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsMoveResult>.Ok>();
        store.Items["morning-news"].AgentId.ShouldBe("home");
    }

    [Fact]
    public async Task Delete_RemovesSchedule()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var result = await fs.DeleteAsync("/jonas/morning-news", CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();
        store.Items.ContainsKey("morning-news").ShouldBeFalse();
    }

    [Fact]
    public async Task Edit_ProducingInvalidSpec_IsRejected()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "summarize news", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        // blank out the prompt value -> ValidateSpec must reject
        var result = await fs.EditAsync("/jonas/morning-news/schedule.json",
            [new TextEdit("\"prompt\": \"summarize news\"", "\"prompt\": \"\"")], CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsEditResult>.Err>();
    }

    [Fact]
    public async Task Move_ToExistingDestinationId_IsRejected()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule { Id = "morning-news", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow });
        await store.CreateAsync(new Schedule { Id = "evening-news", AgentId = "jonas", Prompt = "p", CronExpression = "0 20 * * *", CreatedAt = DateTime.UtcNow });
        var fs = Build(store);

        var result = await fs.MoveAsync("/jonas/morning-news", "/jonas/evening-news", CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsMoveResult>.Err>();
    }

    [Fact]
    public async Task Delete_NonExistent_IsRejected()
    {
        var fs = Build(new FakeScheduleStore());
        var result = await fs.DeleteAsync("/jonas/ghost-schedule", CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsRemoveResult>.Err>();
    }
}