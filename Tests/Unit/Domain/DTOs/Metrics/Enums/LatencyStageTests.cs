using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Metrics.Enums;

public class LatencyStageTests
{
    [Theory]
    [InlineData(LatencyStage.SessionWarmup, 0)]
    [InlineData(LatencyStage.MemoryRecall, 1)]
    [InlineData(LatencyStage.LlmFirstToken, 2)]
    [InlineData(LatencyStage.LlmTotal, 3)]
    [InlineData(LatencyStage.ToolExec, 4)]
    [InlineData(LatencyStage.HistoryStore, 5)]
    [InlineData(LatencyStage.FirstReply, 6)]
    public void LatencyStage_HasPinnedWireValues(LatencyStage stage, int expected)
    {
        ((int)stage).ShouldBe(expected);
    }
}