using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Exceptions;
using Domain.Tools;
using Domain.Tools.Attachments;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Agents;

public class AgentTests
{
    private const string DefaultLibraryPath = "test/library/path";
    private const string DefaultDownloadLocation = "test/library/downloads";
    private readonly Mock<ILargeLanguageModel> _mockLargeLanguageModel = new();
    private readonly Mock<IDownloadClient> _mockDownloadClient = new();
    private readonly Mock<IFileSystemClient> _mockFileSystemClient = new();
    private readonly Mock<ISearchClient> _mockSearchClient = new();
    private readonly SearchHistory _searchHistory = new();

    [Fact]
    public async Task RunAgent_ShouldReturnLlmResponse_WhenNoToolCalls()
    {
        // given
        const string userPrompt = "test prompt";
        var expectedResponse = CreateAgentResponse("This is a test response", StopReason.Stop);
        SetupLlmResponses([[expectedResponse]]);
        var agent = await CreateAgent(10);

        // when
        var responses = await agent.Run(userPrompt).ToArrayAsync();

        // then
        responses.Length.ShouldBe(1);
        responses[0].ShouldBe(expectedResponse);
        VerifyLlmPromptContainsUserMessage(userPrompt);
    }

    [Fact]
    public async Task RunAgent_ShouldExecuteToolCalls_WhenToolCallsAreRequested()
    {
        // given
        const string userPrompt = "search for a file";
        const string toolCallId = "tool-call-id-1";

        var llmResponse = CreateToolCallResponse(
            "I'll search for that file",
            toolCallId,
            "FileSearch",
            new JsonObject
            {
                ["SearchString"] = "test query"
            });

        var finalResponse = CreateAgentResponse("I found your file", StopReason.Stop);

        SetupLlmResponses([[llmResponse], [finalResponse]]);
        SetupSearchClient("test query", "Test File", 1, "https://example.com/file");
        var agent = await CreateAgent(10);

        // when
        var responses = await agent.Run(userPrompt).ToArrayAsync();

        // then
        responses.Length.ShouldBe(2);
        responses[0].ShouldBe(llmResponse);
        responses[1].ShouldBe(finalResponse);
        VerifyLlmPromptContainsToolResponse(toolCallId);
    }

    [Fact]
    public async Task RunAgent_ShouldContinueLoop_WithMultipleToolCalls()
    {
        // given
        const string userPrompt = "search and download a file";
        const string searchToolCallId = "search-tool-id";
        const string downloadToolCallId = "download-tool-id";

        var searchToolResponse = CreateToolCallResponse(
            "I'll search for that file",
            searchToolCallId,
            "FileSearch",
            new JsonObject
            {
                ["SearchString"] = "test file"
            });

        var downloadToolResponse = CreateToolCallResponse(
            "Now I'll download the file",
            downloadToolCallId,
            "FileDownload",
            new JsonObject
            {
                ["SearchResultId"] = 1
            });

        var finalResponse = CreateAgentResponse("File has been downloaded", StopReason.Stop);

        SetupLlmResponses([[searchToolResponse], [downloadToolResponse], [finalResponse]]);
        SetupSearchClient("test file", "Test File", 1, "https://example.com/file");
        SetupDownloadClient(1, "https://example.com/file", $"{DefaultDownloadLocation}/1");
        var agent = await CreateAgent(10);

        // when
        var responses = await agent.Run(userPrompt).ToArrayAsync();

        // then
        responses.Length.ShouldBe(3);
        responses[0].ShouldBe(searchToolResponse);
        responses[1].ShouldBe(downloadToolResponse);
        responses[2].ShouldBe(finalResponse);

        VerifySearchClientCalled("test file");
        VerifyDownloadClientCalled("https://example.com/file", $"{DefaultDownloadLocation}/1", 1);
    }

    [Fact]
    public async Task RunAgent_ShouldCancelPreviousOperation_WhenNewPromptIsSubmitted()
    {
        // given
        const string firstPrompt = "first prompt";
        const string secondPrompt = "second prompt";

        var firstResponse = CreateAgentResponse("First response", StopReason.Stop);
        var secondResponse = CreateAgentResponse("Second response", StopReason.Stop);

        var setup = _mockLargeLanguageModel.Setup(x => x.Prompt(
            It.IsAny<IEnumerable<Message>>(),
            It.IsAny<IEnumerable<ToolDefinition>>(),
            It.IsAny<bool>(),
            It.IsAny<float?>(),
            It.IsAny<CancellationToken>()));
        setup.Returns<IEnumerable<Message>, IEnumerable<ToolDefinition>, bool, float?, CancellationToken>
        (async (_, _, _, _, c) =>
        {
            await Task.Delay(5000, c);
            return [firstResponse];
        });
        var agent = await CreateAgent(10);

        // when
        var firstTask = agent.Run(firstPrompt).ToArrayAsync();
        await Task.Delay(500);
        setup.ReturnsAsync([secondResponse]);
        var secondResponses = await agent.Run(secondPrompt).ToArrayAsync();

        // then
        await Should.ThrowAsync<TaskCanceledException>(async () => await firstTask);
        secondResponses.Length.ShouldBe(1);
        secondResponses[0].ShouldBe(secondResponse);
        VerifyLlmPromptContainsUserMessage(secondPrompt);
    }

    [Fact]
    public async Task RunAgent_ShouldThrowAgentLoopException_WhenMaxDepthIsReached()
    {
        // given
        var agent = await CreateAgent(2);
        const string userPrompt = "test prompt";
        var toolCallResponse = CreateToolCallResponse(
            "Calling tool",
            "tool-call",
            "FileSearch",
            new JsonObject
            {
                ["SearchString"] = "test"
            });

        SetupLlmToolCallsOnly([toolCallResponse]);
        SetupSearchClient("test", "Test File", 1, "https://example.com/file");

        // when/then
        var exception = await Should.ThrowAsync<AgentLoopException>(async () =>
        {
            await agent.Run(userPrompt).ToArrayAsync();
        });

        exception.Message.ShouldContain("max depth (2)");
    }

    #region Helper Methods

    private void SetupLlmResponses(AgentResponse[][] responseSequence)
    {
        var mock = _mockLargeLanguageModel
            .SetupSequence<Task<AgentResponse[]>>(x => x.Prompt(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<bool>(),
                It.IsAny<float?>(),
                It.IsAny<CancellationToken>()));

        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var item in responseSequence)
        {
            mock = mock.ReturnsAsync(item);
        }
    }

    private void SetupLlmToolCallsOnly(AgentResponse[] toolCallResponses)
    {
        _mockLargeLanguageModel
            .Setup(x => x.Prompt(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<bool>(),
                It.IsAny<float?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(toolCallResponses);
    }

    private async Task<Agent> CreateAgent(int maxDepth)
    {
        return new Agent(
            await new DownloaderPrompt().Get(null),
            _mockLargeLanguageModel.Object,
            [
                new FileSearchTool(_mockSearchClient.Object, _searchHistory),
                new FileDownloadTool(_mockDownloadClient.Object, _searchHistory,
                    DefaultDownloadLocation),
                new WaitForDownloadTool(_mockDownloadClient.Object),
                new LibraryDescriptionTool(_mockFileSystemClient.Object, DefaultLibraryPath),
                new MoveTool(_mockFileSystemClient.Object, DefaultLibraryPath),
                new CleanupTool(_mockDownloadClient.Object, _mockFileSystemClient.Object, DefaultDownloadLocation)
            ],
            maxDepth,
            true,
            NullLogger<Agent>.Instance
        );
    }

    private void SetupSearchClient(string query, string title, int id, string link)
    {
        _mockSearchClient
            .Setup(x => x.Search(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SearchResult
                {
                    Title = title,
                    Id = id,
                    Link = link
                }
            ]);
    }

    private void SetupDownloadClient(int id, string link, string savePath)
    {
        _mockDownloadClient
            .Setup(x => x.Download(link, savePath, id, It.IsAny<CancellationToken>()));
    }

    private void VerifyLlmPromptContainsUserMessage(string userPrompt)
    {
        _mockLargeLanguageModel.Verify(x => x.Prompt(
            It.Is<IEnumerable<Message>>(m => m.Any(msg => msg.Role == Role.User && msg.Content == userPrompt)),
            It.IsAny<IEnumerable<ToolDefinition>>(),
            It.IsAny<bool>(),
            It.IsAny<float?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private void VerifyLlmPromptContainsToolResponse(string toolCallId)
    {
        _mockLargeLanguageModel.Verify(x => x.Prompt(
            It.Is<IEnumerable<Message>>(m => m.Any(msg => msg is ToolMessage &&
                                                          msg.Role == Role.Tool &&
                                                          ((ToolMessage)msg).ToolCallId == toolCallId)),
            It.IsAny<IEnumerable<ToolDefinition>>(),
            It.IsAny<bool>(),
            It.IsAny<float?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private void VerifySearchClientCalled(string query)
    {
        _mockSearchClient.Verify(
            x => x.Search(query, It.IsAny<CancellationToken>()), Times.Once);
    }

    private void VerifyDownloadClientCalled(string link, string savePath, int id)
    {
        _mockDownloadClient.Verify(
            x => x.Download(
                link,
                savePath,
                id,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static AgentResponse CreateAgentResponse(string content, StopReason stopReason)
    {
        return new AgentResponse
        {
            Role = Role.Assistant,
            Content = content,
            StopReason = stopReason
        };
    }

    private static AgentResponse CreateToolCallResponse(
        string content, string toolCallId, string toolName, JsonNode parameters)
    {
        return new AgentResponse
        {
            Role = Role.Assistant,
            Content = content,
            StopReason = StopReason.ToolCalls,
            ToolCalls =
            [
                new ToolCall
                {
                    Id = toolCallId,
                    Name = toolName,
                    Parameters = parameters
                }
            ]
        };
    }

    #endregion
}