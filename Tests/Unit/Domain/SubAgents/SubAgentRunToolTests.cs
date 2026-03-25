using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.SubAgents;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.SubAgents;

public class SubAgentRunToolTests
{
    private readonly Mock<ISubAgentRunner> _runner = new();
    private readonly Mock<ISubAgentContextAccessor> _contextAccessor = new();

    private static readonly SubAgentDefinition TestProfile = new()
    {
        Id = "summarizer",
        Name = "Summarizer",
        Description = "Summarizes content",
        Model = "test-model",
        McpServerEndpoints = []
    };

    private SubAgentRunTool CreateTool(params SubAgentDefinition[] profiles) =>
        new(_runner.Object, _contextAccessor.Object,
            new SubAgentRegistryOptions { SubAgents = profiles });

    [Fact]
    public async Task RunAsync_UnknownProfile_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.RunAsync("unknown", "do something", "parent");

        result["status"]!.GetValue<string>().ShouldBe("error");
        result["error"]!.GetValue<string>().ShouldContain("unknown");
    }

    [Fact]
    public async Task RunAsync_MissingContext_ReturnsError()
    {
        _contextAccessor.Setup(a => a.GetContext("parent")).Returns((SubAgentContext?)null);
        var tool = CreateTool(TestProfile);

        var result = await tool.RunAsync("summarizer", "do something", "parent");

        result["status"]!.GetValue<string>().ShouldBe("error");
        result["error"]!.GetValue<string>().ShouldContain("context");
    }

    [Fact]
    public async Task RunAsync_ValidProfile_CallsRunnerAndReturnsResult()
    {
        var context = new SubAgentContext(
            Mock.Of<IToolApprovalHandler>(), ["pattern:*"], "user-1");
        _contextAccessor.Setup(a => a.GetContext("parent")).Returns(context);
        _runner.Setup(r => r.RunAsync(TestProfile, "summarize this", context, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Summary result");

        var tool = CreateTool(TestProfile);

        var result = await tool.RunAsync("summarizer", "summarize this", "parent");

        result["status"]!.GetValue<string>().ShouldBe("completed");
        result["result"]!.GetValue<string>().ShouldBe("Summary result");
    }

    [Fact]
    public async Task RunAsync_RunnerThrows_ReturnsError()
    {
        var context = new SubAgentContext(
            Mock.Of<IToolApprovalHandler>(), [], "user-1");
        _contextAccessor.Setup(a => a.GetContext("parent")).Returns(context);
        _runner.Setup(r => r.RunAsync(It.IsAny<SubAgentDefinition>(), It.IsAny<string>(),
                It.IsAny<SubAgentContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("timed out"));

        var tool = CreateTool(TestProfile);

        var result = await tool.RunAsync("summarizer", "do something", "parent");

        result["status"]!.GetValue<string>().ShouldBe("error");
        result["error"]!.GetValue<string>().ShouldContain("timed out");
    }
}
