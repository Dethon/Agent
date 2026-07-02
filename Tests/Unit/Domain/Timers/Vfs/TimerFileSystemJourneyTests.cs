using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Timers;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Timers.Vfs;

public class TimerFileSystemJourneyTests
{
    private static readonly TimeZoneInfo _madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static (TimerFileSystem Fs, InMemoryTimerStore Store, FakeTimeProvider Time) Build()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(_madrid);
        var store = new InMemoryTimerStore();
        return (new TimerFileSystem(store, time), store, time);
    }

    private const string PastaSpec = """
        {"durationSeconds": 300, "text": "pasta is ready", "target": {"room": "Kitchen"}}
        """;

    [Fact]
    public async Task CreateReadStatusCancel_FullJourney()
    {
        var (fs, _, time) = Build();

        var created = await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);
        created.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        var glob = (await fs.GlobAsync("/", "**", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        glob.Entries.ShouldBe(["/pasta/", "/pasta/status.json", "/pasta/timer.json"]);

        var spec = (await fs.ReadAsync("/pasta/timer.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        spec.Content.ShouldContain("\"durationSeconds\": 300");
        spec.Content.ShouldContain("pasta is ready");

        time.Advance(TimeSpan.FromSeconds(100));
        var status = (await fs.ReadAsync("/pasta/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        status.Content.ShouldContain("\"remainingSeconds\": 200");
        status.Content.ShouldContain("+02:00"); // firesAt rendered in the operating zone (CEST)

        var deleted = await fs.DeleteAsync("/pasta", CancellationToken.None);
        deleted.ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();
        (await fs.ReadAsync("/pasta/timer.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>();
    }

    [Fact]
    public async Task Create_InvalidDuration_IsRejected()
    {
        var (fs, _, _) = Build();

        var result = await fs.CreateAsync(
            "/bad/timer.json", """{"durationSeconds": 0, "target": {"room": "Kitchen"}}""", false, true, CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.Message.ShouldContain("durationSeconds");
    }

    [Fact]
    public async Task Create_MissingTarget_IsRejected()
    {
        var (fs, _, _) = Build();

        var result = await fs.CreateAsync(
            "/bad/timer.json", """{"durationSeconds": 60}""", false, true, CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.Message.ShouldContain("target");
    }

    [Fact]
    public async Task Create_DuplicateId_IsRejected()
    {
        var (fs, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        var result = await fs.CreateAsync("/pasta/timer.json", PastaSpec, true, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
    }

    [Fact]
    public async Task Create_WrongPathShape_IsRejected()
    {
        var (fs, _, _) = Build();

        var result = await fs.CreateAsync("/pasta.json", PastaSpec, false, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
    }

    [Fact]
    public async Task Edit_IsUnsupported_TimersAreImmutable()
    {
        var (fs, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        var result = await fs.EditAsync("/pasta/timer.json",
            [new TextEdit("300", "600")], CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsEditResult>.Err>();
        err.Error.Message.ShouldContain("immutable");
    }

    [Fact]
    public async Task Delete_TimerJsonFile_IsRejected_DirIsTheUnit()
    {
        var (fs, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        (await fs.DeleteAsync("/pasta/timer.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>();
    }

    [Fact]
    public async Task Search_FindsTimerSpecContent()
    {
        var (fs, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        var result = (await fs.SearchAsync(
                "pasta is ready", false, null, null, null, 10, 0,
                VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;

        result.TotalMatches.ShouldBe(1);
        result.Results[0].File.ShouldBe("/pasta/timer.json");
    }
}