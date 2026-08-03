using Microsoft.Extensions.Logging;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Extensions;

namespace Tests.Unit.WebChat.Client.Extensions;

public sealed class TaskExtensionsTests
{
    private readonly RecordingLogger _logger = new();

    [Fact]
    public async Task LogFaults_TaskFaultsLater_LogsTheException()
    {
        var source = new TaskCompletionSource();

        source.Task.LogFaults(_logger);
        source.SetException(new InvalidOperationException("boom"));

        var entry = await _logger.WaitForEntryAsync();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("boom");
    }

    [Fact]
    public async Task LogFaults_TaskAlreadyFaulted_LogsTheException()
    {
        var faulted = Task.FromException(new InvalidOperationException("already broken"));

        faulted.LogFaults(_logger);

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("already broken");
    }

    [Fact]
    public async Task LogFaults_ContextGiven_NamesItInTheMessage()
    {
        var faulted = Task.FromException(new InvalidOperationException("boom"));

        faulted.LogFaults(_logger, "InitializationEffect.Initialize");

        var entry = await _logger.WaitForEntryAsync();
        entry.Message.ShouldContain("InitializationEffect.Initialize");
    }

    [Fact]
    public async Task LogFaults_TaskCompletes_LogsNothing()
    {
        var source = new TaskCompletionSource();

        source.Task.LogFaults(_logger);
        source.SetResult();
        await source.Task;

        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void LogFaults_TaskStillRunning_ReturnsWithoutBlockingOrLogging()
    {
        var pending = new TaskCompletionSource().Task;

        Should.NotThrow(() => pending.LogFaults(_logger));

        _logger.Entries.ShouldBeEmpty();
    }
}