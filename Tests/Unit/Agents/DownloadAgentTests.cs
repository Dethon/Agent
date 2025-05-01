using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using Domain.Tools.Attachments;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Unit.Agents;

public class DownloadAgentTests
{
    private const string DefaultLibraryPath = "test/library/path";
    private const string DefaultDownloadLocation = "test/library/downloads";
    private readonly Agent _agent;
    private readonly Mock<ILargeLanguageModel> _mockLargeLanguageModel = new();
    private readonly Mock<IDownloadClient> _mockDownloadClient = new();
    private readonly Mock<IFileSystemClient> _mockFileSystemClient = new();
    private readonly Mock<ISearchClient> _mockSearchClient = new();
    private readonly SearchHistory _searchHistory = new();
    private readonly DownloadMonitor _downloadMonitor;

    public DownloadAgentTests()
    {
        _downloadMonitor = new DownloadMonitor(_mockDownloadClient.Object);
        _agent = new Agent(
            DownloadSystemPrompt.Prompt,
            _mockLargeLanguageModel.Object,
            [
                new FileSearchTool(_mockSearchClient.Object, _searchHistory),
                new FileDownloadTool(_mockDownloadClient.Object, _searchHistory, _downloadMonitor,
                    DefaultDownloadLocation),
                new WaitForDownloadTool(_downloadMonitor),
                new LibraryDescriptionTool(_mockFileSystemClient.Object, DefaultLibraryPath),
                new MoveTool(_mockFileSystemClient.Object, DefaultLibraryPath),
                new CleanupTool(_mockDownloadClient.Object, _mockFileSystemClient.Object, DefaultDownloadLocation)
            ],
            10,
            true,
            NullLogger<Agent>.Instance
        );
    }

    [Fact]
    public async Task RunAgen_ShouldOrchestrateCorrectly()
    {
        // given
        const string userPrompt = "test prompt";
        SetupLlmResponses([[]]);

        // when
        var responses = await _agent.Run(userPrompt).ToArrayAsync();

        // then
    }

    #region MyRegion

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

    #endregion
}