using System.Globalization;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Validation;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

// Journey-style tests that exercise ScheduleFileSystem end-to-end as a FileSystemToolFeature
// consumer would. Each fact covers a real-world scenario rather than a single backend method.
public class ScheduleFileSystemJourneyTests
{
    private const string ValidSpec = """{"prompt":"summarize news","cron":"0 8 * * *"}""";

    private static ScheduleFileSystem Build(
        FakeScheduleStore? store = null,
        params AgentCatalogEntry[] agents)
    {
        var catalog = new MutableAgentCatalog();
        var entries = agents.Length == 0
            ? new[] { new AgentCatalogEntry("jonas", "Jonas", "general") }
            : agents;
        catalog.Replace(entries);
        return new ScheduleFileSystem(store ?? new FakeScheduleStore(), catalog, new CronValidator());
    }

    private static Schedule SeedSchedule(string id = "morning-news", string agentId = "jonas",
        string prompt = "summarize news", string cron = "0 8 * * *",
        DateTime? nextRunAt = null, DateTime? lastRunAt = null) =>
        new()
        {
            Id = id,
            AgentId = agentId,
            Prompt = prompt,
            CronExpression = cron,
            NextRunAt = nextRunAt,
            LastRunAt = lastRunAt,
            CreatedAt = DateTime.UtcNow
        };

    [Fact]
    public async Task Backend_Contract_ExposesNameAndUnsupportedOps()
    {
        var fs = Build();

        fs.ShouldBeAssignableTo<IFileSystemBackend>();
        fs.FilesystemName.ShouldBe("schedules");

        var copy = await fs.CopyAsync("/jonas/a", "/jonas/b", false, true, CancellationToken.None);
        copy.ShouldBeOfType<FsResult<FsCopyResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        Should.Throw<NotSupportedException>(() =>
        {
            fs.ReadChunksAsync("/jonas/a/schedule.json", CancellationToken.None);
        });

        await Should.ThrowAsync<NotSupportedException>(() =>
            fs.WriteChunksAsync("/jonas/a/schedule.json", AsyncEmpty(), false, true, CancellationToken.None));
    }

    [Fact]
    public async Task Lifecycle_CreateGlobReadDelete_RoundTripsSchedule()
    {
        var store = new FakeScheduleStore();
        var fs = Build(store,
            new AgentCatalogEntry("jonas", "Jonas", "general"),
            new AgentCatalogEntry("jack", "Jack", "library"));

        // Create — agentId is inferred from the path, NextRunAt is computed from cron.
        var create = await fs.CreateAsync("/jonas/morning-news/schedule.json", ValidSpec, false, true, CancellationToken.None);
        create.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        var saved = store.Items["morning-news"];
        saved.AgentId.ShouldBe("jonas");
        saved.CronExpression.ShouldBe("0 8 * * *");
        saved.NextRunAt.ShouldNotBeNull();

        // Glob root — every catalog agent shows up, even ones with no schedules.
        var rootGlob = (await fs.GlobAsync("/", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        rootGlob.Entries.ShouldContain("/jonas");
        rootGlob.Entries.ShouldContain("/jack");

        // Glob agent dir — lists this agent's schedules.
        var agentGlob = (await fs.GlobAsync("/jonas", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        agentGlob.Entries.ShouldContain("/jonas/morning-news");

        // Read schedule.json — spec is rendered without agentId (path already encodes it).
        var readSchedule = (await fs.ReadAsync("/jonas/morning-news/schedule.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        readSchedule.Content.ShouldContain("summarize news");
        readSchedule.Content.ShouldNotContain("agentId");

        // Read agent_info.json — metadata, includes id + display name.
        var readInfo = (await fs.ReadAsync("/jonas/agent_info.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        readInfo.Content.ShouldContain("\"id\"");
        readInfo.Content.ShouldContain("Jonas");

        // Info on agent dir — reports directory.
        var info = (await fs.InfoAsync("/jonas", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        info.Exists.ShouldBeTrue();
        info.IsDirectory.ShouldBe(true);

        // Delete — schedule disappears from the store.
        var delete = await fs.DeleteAsync("/jonas/morning-news", CancellationToken.None);
        delete.ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();
        store.Items.ContainsKey("morning-news").ShouldBeFalse();
    }

    [Fact]
    public async Task UnknownAgent_ReadsAndCreatesAndInfo_AllFailGracefully()
    {
        var fs = Build();

        (await fs.CreateAsync("/ghost/x/schedule.json", ValidSpec, false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Err>();

        (await fs.ReadAsync("/ghost/x/schedule.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>();

        var info = (await fs.InfoAsync("/ghost", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        info.Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task Edit_PromptAndCron_UpdatesSpecAndRecomputesNextRunAtAppropriately()
    {
        var nextRun = new DateTime(2999, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var store = new FakeScheduleStore();
        await store.CreateAsync(SeedSchedule(prompt: "summarize news", nextRunAt: nextRun));
        var fs = Build(store);

        // Editing the prompt only — content updates, NextRunAt is preserved.
        var promptEdit = await fs.EditAsync("/jonas/morning-news/schedule.json",
            [new TextEdit("summarize news", "summarize sports")], CancellationToken.None);
        promptEdit.ShouldBeOfType<FsResult<FsEditResult>.Ok>();
        store.Items["morning-news"].Prompt.ShouldBe("summarize sports");
        store.Items["morning-news"].NextRunAt.ShouldBe(nextRun);

        // Editing the cron — NextRunAt must be recomputed (no longer equal to the stale value).
        await fs.EditAsync("/jonas/morning-news/schedule.json",
            [new TextEdit("0 8 * * *", "0 9 * * *")], CancellationToken.None);
        store.Items["morning-news"].CronExpression.ShouldBe("0 9 * * *");
        store.Items["morning-news"].NextRunAt.ShouldNotBe(nextRun);
    }

    [Fact]
    public async Task Edit_ReadOnlyOrInvalid_IsRejectedWithProperErrorCode()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(SeedSchedule());
        var fs = Build(store);

        // Producing an invalid spec (empty prompt) — validator must reject.
        var invalid = await fs.EditAsync("/jonas/morning-news/schedule.json",
            [new TextEdit("\"prompt\": \"summarize news\"", "\"prompt\": \"\"")], CancellationToken.None);
        invalid.ShouldBeOfType<FsResult<FsEditResult>.Err>();

        // status.json is read-only.
        var status = await fs.EditAsync("/jonas/morning-news/status.json",
            [new TextEdit("a", "b")], CancellationToken.None);
        status.ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);

        // agent_info.json is read-only.
        var info = await fs.EditAsync("/jonas/agent_info.json",
            [new TextEdit("a", "b")], CancellationToken.None);
        info.ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);

        // Editing a nonexistent schedule surfaces not_found.
        var missing = await fs.EditAsync("/jonas/ghost/schedule.json",
            [new TextEdit("a", "b")], CancellationToken.None);
        missing.ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
    }

    [Fact]
    public async Task Move_ReassignsAgent_OrFailsOnConflicts_AndRespectsReadOnlyDeletes()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(SeedSchedule());
        await store.CreateAsync(SeedSchedule(id: "evening-news", prompt: "p", cron: "0 20 * * *"));
        var fs = Build(store,
            new AgentCatalogEntry("jonas", "Jonas", null),
            new AgentCatalogEntry("home", "Home", null));

        // Move across agents — schedule keeps its id but the agent is reassigned.
        var move = await fs.MoveAsync("/jonas/morning-news", "/home/morning-news", CancellationToken.None);
        move.ShouldBeOfType<FsResult<FsMoveResult>.Ok>();
        store.Items["morning-news"].AgentId.ShouldBe("home");

        // Move into an already-used id — rejected.
        var conflict = await fs.MoveAsync("/home/morning-news", "/jonas/evening-news", CancellationToken.None);
        conflict.ShouldBeOfType<FsResult<FsMoveResult>.Err>();

        // status.json is read-only — delete must fail with UnsupportedOperation.
        var deleteStatus = await fs.DeleteAsync("/jonas/evening-news/status.json", CancellationToken.None);
        deleteStatus.ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);

        // Deleting a schedule that doesn't exist must fail too.
        var deleteGhost = await fs.DeleteAsync("/jonas/ghost-schedule", CancellationToken.None);
        deleteGhost.ShouldBeOfType<FsResult<FsRemoveResult>.Err>();
    }

    [Fact]
    public async Task Create_RejectsDuplicateIdConflictingTriggersAndUnzonedRunAt()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(SeedSchedule());
        var fs = Build(store);

        // Duplicate id (same path) — rejected.
        var duplicate = await fs.CreateAsync("/jonas/morning-news/schedule.json", ValidSpec, false, true, CancellationToken.None);
        duplicate.ShouldBeOfType<FsResult<FsCreateResult>.Err>();

        // Specifying both cron AND runAt — rejected.
        var both = await fs.CreateAsync("/jonas/x/schedule.json",
            """{"prompt":"p","cron":"0 8 * * *","runAt":"2999-01-01T00:00:00Z"}""",
            false, true, CancellationToken.None);
        both.ShouldBeOfType<FsResult<FsCreateResult>.Err>();

        // runAt without a timezone — rejected (no zone).
        var noZone = await fs.CreateAsync("/jonas/once/schedule.json",
            """{"prompt":"p","runAt":"2999-01-01T00:00:00"}""",
            false, true, CancellationToken.None);
        noZone.ShouldBeOfType<FsResult<FsCreateResult>.Err>();

        // runAt without a timezone — rejected (no zone, fractional seconds).
        var noZoneFraction = await fs.CreateAsync("/jonas/once/schedule.json",
            """{"prompt":"p","runAt":"2999-01-01T14:30:00.5"}""",
            false, true, CancellationToken.None);
        noZoneFraction.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
    }

    [Theory]
    [InlineData("2999-07-15T14:30:00Z", "2999-07-15T14:30:00Z")]       // UTC, unchanged
    [InlineData("2999-07-15T14:30:00+00:00", "2999-07-15T14:30:00Z")]  // explicit zero offset
    [InlineData("2999-07-15T14:30:00+05:00", "2999-07-15T09:30:00Z")]  // ahead of UTC
    [InlineData("2999-07-15T14:30:00-08:00", "2999-07-15T22:30:00Z")]  // behind UTC
    [InlineData("2999-01-15T14:30:00+02:00", "2999-01-15T12:30:00Z")]  // winter offset
    public async Task Create_ZonedRunAt_NormalizesToUtc(string runAtInput, string expectedUtc)
    {
        var store = new FakeScheduleStore();
        var fs = Build(store);
        var spec = $$"""{"prompt":"p","runAt":"{{runAtInput}}"}""";

        var result = await fs.CreateAsync("/jonas/once/schedule.json", spec, false, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        var expected = DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var saved = store.Items["once"];
        saved.RunAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        saved.RunAt.ShouldBe(expected);
        saved.NextRunAt.ShouldBe(expected);
    }

    [Fact]
    public async Task Search_HonorsModesScopesRegexAndTruncation()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(SeedSchedule(prompt: "summarize the news"));
        await store.CreateAsync(SeedSchedule(id: "weather", agentId: "jack",
            prompt: "the weather report", cron: "0 7 * * *"));
        var fs = Build(store,
            new AgentCatalogEntry("jonas", "Jonas", "general"),
            new AgentCatalogEntry("jack", "Jack", "library"));

        // Content mode — returns line-level matches with non-null Line and null MatchCount.
        var content = (await fs.SearchAsync("news", false, null, null, null, 50, 1,
                VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        var contentFile = content.Results.ShouldHaveSingleItem();
        contentFile.File.ShouldBe("/jonas/morning-news/schedule.json");
        var contentMatch = contentFile.Matches.ShouldNotBeNull().ShouldHaveSingleItem();
        contentMatch.Text.ShouldContain("summarize the news");
        contentMatch.Line.ShouldBeGreaterThan(0);
        contentFile.MatchCount.ShouldBeNull();

        // FilesOnly mode — returns MatchCount, no per-line matches.
        var filesOnly = (await fs.SearchAsync("news", false, null, null, null, 50, 1,
                VfsTextSearchOutputMode.FilesOnly, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        var filesOnlyFile = filesOnly.Results.ShouldHaveSingleItem();
        filesOnlyFile.MatchCount.ShouldBe(1);
        filesOnlyFile.Matches.ShouldBeNull();

        // The result echoes the regex flag, query, and scope back to the caller.
        var echoed = (await fs.SearchAsync("news", true, null, "/jonas", null, 50, 1,
                VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        echoed.Regex.ShouldBeTrue();
        echoed.Query.ShouldBe("news");
        echoed.Path.ShouldBe("/jonas");

        // directoryPath scopes search to the chosen agent only.
        var scoped = (await fs.SearchAsync("the", false, null, "/jack", null, 50, 1,
                VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        scoped.FilesSearched.ShouldBe(1);
        scoped.Results.ShouldHaveSingleItem().File.ShouldBe("/jack/weather/schedule.json");

        // Regex queries are honored (alternation matches both files).
        var regex = (await fs.SearchAsync("news|weather", true, null, null, null, 50, 1,
                VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        regex.Results.Count.ShouldBe(2);

        // maxResults caps matches and surfaces Truncated.
        var singletonStore = new FakeScheduleStore();
        await singletonStore.CreateAsync(SeedSchedule(id: "s", prompt: "p"));
        var singletonFs = Build(singletonStore);
        var truncated = (await singletonFs.SearchAsync("null", false, null, null, null, 1, 1,
                VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        truncated.TotalMatches.ShouldBe(1);
        truncated.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task Exec_RunNow_TriggersImmediateFire_AndUnknownCommandsOrSchedulesFail()
    {
        var future = DateTime.UtcNow.AddDays(1);
        var lastRun = DateTime.UtcNow.AddHours(-3);

        // run_now.sh on a never-run schedule — marks it due (NextRunAt rolled back) but LastRunAt stays null.
        var neverRunStore = new FakeScheduleStore();
        await neverRunStore.CreateAsync(SeedSchedule(id: "n", prompt: "p", nextRunAt: future));
        var neverRunFs = Build(neverRunStore);

        var neverRun = await neverRunFs.ExecAsync("/jonas/n", "run_now.sh", null, CancellationToken.None);
        var neverRunResult = neverRun.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        neverRunResult.ExitCode.ShouldBe(0);
        (neverRunStore.Items["n"].NextRunAt <= DateTime.UtcNow).ShouldBeTrue();
        neverRunStore.Items["n"].LastRunAt.ShouldBeNull();

        // run_now.sh preserves an existing LastRunAt (it represents the last actual fire, not this trigger).
        var alreadyRunStore = new FakeScheduleStore();
        await alreadyRunStore.CreateAsync(SeedSchedule(id: "n", prompt: "p", nextRunAt: future, lastRunAt: lastRun));
        var alreadyRunFs = Build(alreadyRunStore);
        await alreadyRunFs.ExecAsync("/jonas/n", "run_now.sh", null, CancellationToken.None);
        alreadyRunStore.Items["n"].LastRunAt.ShouldBe(lastRun);

        // An unknown command on an existing schedule returns 127 with a hint about run_now.sh.
        var unknownCommandStore = new FakeScheduleStore();
        await unknownCommandStore.CreateAsync(SeedSchedule(id: "n", prompt: "p"));
        var unknownCommandFs = Build(unknownCommandStore);
        var unknownCommand = await unknownCommandFs.ExecAsync("/jonas/n", "ls -la", null, CancellationToken.None);
        var unknownExec = unknownCommand.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        unknownExec.ExitCode.ShouldBe(127);
        unknownExec.Stderr.ShouldContain("run_now.sh");

        // run_now.sh on a schedule that doesn't exist returns Err (not_found).
        var ghostFs = Build();
        (await ghostFs.ExecAsync("/jonas/ghost", "run_now.sh", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Err>();
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> AsyncEmpty()
    {
        await Task.CompletedTask;
        yield break;
    }
}