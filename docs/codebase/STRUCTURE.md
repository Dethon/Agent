# Project Structure

## Directory Layout

```
agent/
+-- Agent/                    # Composition root (entry point)
|   +-- App/                  # Background services
|   |   +-- ChatMonitoring.cs       # Drives ChatMonitor in a loop
|   |   +-- ScheduleMonitoring.cs   # Runs ScheduleDispatcher + ScheduleExecutor
|   +-- Hubs/                 # SignalR hubs
|   |   +-- ChatHub.cs              # WebChat SignalR hub (sessions, messaging, approvals)
|   |   +-- HubNotificationAdapter.cs  # IHubNotificationSender -> IHubContext bridge
|   +-- Modules/              # DI configuration
|   |   +-- ConfigModule.cs          # Settings loading + command-line parsing
|   |   +-- InjectorModule.cs        # Core DI: agents, clients, Redis, messaging
|   |   +-- SchedulingModule.cs      # Scheduling DI: channel, stores, tools, executor
|   +-- Settings/             # Configuration classes
|   |   +-- AgentSettings.cs         # Root settings (OpenRouter, Telegram, Redis, ServiceBus, Agents)
|   |   +-- CommandLineParams.cs     # CLI params + ChatInterface enum
|   |   +-- ServiceBusSettings.cs    # Service Bus connection config
|   +-- Program.cs                   # Minimal API entry point
|
+-- Domain/                   # Pure business logic (no external deps)
|   +-- Agents/               # Agent abstractions
|   |   +-- AgentKey.cs              # Chat/thread/agent identifier
|   |   +-- ChatThreadContext.cs     # Per-thread CancellationTokenSource + completion callback
|   |   +-- ChatThreadResolver.cs    # Thread context registry (resolve, cancel, clear)
|   |   +-- DisposableAgent.cs       # Abstract base: AIAgent + IAsyncDisposable
|   +-- Contracts/            # Interfaces consumed by Domain
|   |   +-- IAgentDefinitionProvider.cs
|   |   +-- IAgentFactory.cs
|   |   +-- IAvailableShell.cs
|   |   +-- ICaptchaSolver.cs
|   |   +-- IChatMessengerClient.cs
|   |   +-- ICommandRunner.cs
|   |   +-- ICronValidator.cs
|   |   +-- IDomainToolFeature.cs
|   |   +-- IDomainToolRegistry.cs
|   |   +-- IDownloadClient.cs
|   |   +-- IEmbeddingService.cs
|   |   +-- IFileSystemClient.cs
|   |   +-- IHubNotificationSender.cs
|   |   +-- IIdealistaClient.cs
|   |   +-- IMemoryStore.cs
|   |   +-- IMessageSourceRouter.cs
|   |   +-- INotifier.cs
|   |   +-- IScheduleAgentFactory.cs
|   |   +-- IScheduleStore.cs
|   |   +-- ISearchClient.cs
|   |   +-- ISearchResultsManager.cs
|   |   +-- IThreadStateStore.cs
|   |   +-- IToolApprovalHandler.cs
|   |   +-- IToolApprovalHandlerFactory.cs
|   |   +-- ITrackedDownloadsManager.cs
|   |   +-- IWebBrowser.cs
|   |   +-- IWebSearchClient.cs
|   |   +-- ModalDismissalConfig.cs
|   +-- DTOs/                 # Data transfer objects (records)
|   |   +-- AgentDefinition.cs       # Agent config DTO
|   |   +-- AiResponse.cs            # Streaming response chunk
|   |   +-- ChatPrompt.cs            # Inbound user prompt
|   |   +-- ChatResponseMessage.cs   # Formatted response for CLI
|   |   +-- DownloadItem.cs          # Download status
|   |   +-- Memory.cs               # Memory entry
|   |   +-- MessageSource.cs        # Enum: WebUi, ServiceBus, Telegram, Cli
|   |   +-- ParseResult.cs          # Generic parse success/failure
|   |   +-- ParsedServiceBusMessage.cs
|   |   +-- PersonalityProfile.cs
|   |   +-- ResubscribeResult.cs
|   |   +-- Schedule.cs             # Scheduled task definition
|   |   +-- SearchResult.cs
|   |   +-- ServiceBusPromptMessage.cs
|   |   +-- ServiceBusResponseMessage.cs
|   |   +-- StreamCompleteContent.cs  # Sentinel for stream completion
|   |   +-- ToolApprovalRequest.cs
|   |   +-- ToolApprovalResult.cs
|   |   +-- WebChat/                 # WebChat-specific DTOs
|   |       +-- AgentInfo.cs
|   |       +-- ChatHistoryMessage.cs
|   |       +-- ChatStreamMessage.cs
|   |       +-- HubNotification.cs   # SignalR notification types
|   |       +-- StreamState.cs
|   |       +-- ToolApprovalRequestMessage.cs
|   |       +-- TopicMetadata.cs
|   |       +-- UserMessageInfo.cs
|   |       +-- WebTypes.cs
|   +-- Extensions/           # Extension methods
|   |   +-- AgentRunResponseExtensions.cs
|   |   +-- ChatMessageExtensions.cs    # SenderId/Timestamp on ChatMessage
|   |   +-- IAsyncEnumerableExtensions.cs  # Merge, GroupByStreaming, WithErrorHandling
|   |   +-- JsonElementExtensions.cs
|   |   +-- SemaphoreSlimExtensions.cs  # WithLockAsync
|   |   +-- StringExtensions.cs
|   +-- Monitor/              # Chat monitoring and scheduling
|   |   +-- ChatCommand.cs          # /clear, /cancel command parsing
|   |   +-- ChatMonitor.cs          # Main message processing loop
|   |   +-- ScheduleDispatcher.cs   # Finds due schedules, writes to channel
|   |   +-- ScheduleExecutor.cs     # Reads channel, creates agent, executes prompt
|   +-- Prompts/              # System prompts
|   |   +-- BasePrompt.cs
|   |   +-- DownloaderPrompt.cs
|   |   +-- IdealistaPrompt.cs
|   |   +-- KnowledgeBasePrompt.cs
|   |   +-- MemoryPrompt.cs
|   |   +-- WebBrowsingPrompt.cs
|   +-- Resources/            # Resource definitions
|   |   +-- DownloadResource.cs
|   +-- Routers/              # Message routing logic
|   |   +-- MessageSourceRouter.cs  # WebUI always receives + source-matching client
|   +-- Tools/                # Business logic tools
|       +-- Commands/
|       |   +-- GetCliPlatformTool.cs
|       |   +-- RunCommandTool.cs
|       +-- Config/
|       |   +-- BaseLibraryPathConfig.cs
|       |   +-- DownloadPathConfig.cs
|       +-- ContentRecommendationTool.cs
|       +-- Downloads/
|       |   +-- CleanupDownloadTool.cs
|       |   +-- FileDownloadTool.cs
|       |   +-- GetDownloadStatusTool.cs
|       |   +-- ResubscribeDownloadsTool.cs
|       +-- Files/
|       |   +-- FileSearchTool.cs
|       |   +-- GlobFilesTool.cs
|       |   +-- GlobMode.cs
|       |   +-- MoveTool.cs
|       |   +-- RemoveTool.cs
|       +-- Memory/
|       |   +-- ForgetMode.cs
|       |   +-- MemoryForgetTool.cs
|       |   +-- MemoryListTool.cs
|       |   +-- MemoryRecallTool.cs
|       |   +-- MemoryReflectTool.cs
|       |   +-- MemoryStoreTool.cs
|       +-- RealEstate/
|       |   +-- PropertySearchTool.cs
|       +-- Scheduling/
|       |   +-- ScheduleCreateTool.cs
|       |   +-- ScheduleDeleteTool.cs
|       |   +-- ScheduleListTool.cs
|       |   +-- SchedulingToolFeature.cs  # IDomainToolFeature implementation
|       +-- Text/
|       |   +-- SearchOutputMode.cs
|       |   +-- TextCreateTool.cs
|       |   +-- TextEditTool.cs
|       |   +-- TextReadTool.cs
|       |   +-- TextSearchTool.cs
|       |   +-- TextToolBase.cs
|       +-- Web/
|           +-- WebBrowseTool.cs
|           +-- WebClickTool.cs
|           +-- WebInspectTool.cs
|           +-- WebSearchTool.cs
|
+-- Infrastructure/           # Implementations
|   +-- Agents/               # Agent implementations
|   |   +-- AgentDefinitionProvider.cs  # IAgentDefinitionProvider from IOptionsMonitor
|   |   +-- DomainToolRegistry.cs       # IDomainToolRegistry implementation
|   |   +-- McpAgent.cs                 # Main agent: MCP + LLM + streaming
|   |   +-- MultiAgentFactory.cs        # IAgentFactory + IScheduleAgentFactory
|   |   +-- ThreadSession.cs            # Per-thread MCP session + builder
|   |   +-- ChatClients/               # LLM client wrappers
|   |   |   +-- OpenRouterChatClient.cs      # IChatClient via OpenAI SDK + reasoning extraction
|   |   |   +-- OpenRouterHttpHelpers.cs     # SSE reasoning tee + empty content fix
|   |   |   +-- RedisChatMessageStore.cs     # ChatHistoryProvider backed by Redis
|   |   |   +-- ToolApprovalChatClient.cs    # FunctionInvokingChatClient with approval gate
|   |   +-- Mappers/
|   |   |   +-- ChatResponseUpdateExtensions.cs  # AgentResponseUpdate -> CreateMessageResult
|   |   +-- Mcp/                        # MCP integration internals
|   |       +-- McpClientManager.cs          # MCP client connections, tool/prompt loading
|   |       +-- McpResourceManager.cs        # Resource subscription lifecycle
|   |       +-- McpSamplingHandler.cs        # MCP sampling request handler
|   |       +-- McpSubscriptionManager.cs    # Resource subscribe/unsubscribe + notifications
|   |       +-- QualifiedMcpTool.cs          # AIFunction wrapper with mcp:{server}:{tool} naming
|   |       +-- ResourceUpdateProcessor.cs   # Reads updated resource, runs agent, writes channel
|   +-- CliGui/               # Terminal UI (Terminal.Gui)
|   |   +-- Abstractions/
|   |   |   +-- ICliChatMessageRouter.cs
|   |   |   +-- ITerminalAdapter.cs
|   |   |   +-- ITerminalSession.cs
|   |   |   +-- IToolApprovalUi.cs
|   |   +-- Rendering/
|   |   |   +-- ChatHistoryMapper.cs     # ChatMessage[] -> display lines
|   |   |   +-- ChatLine.cs
|   |   |   +-- ChatLineType.cs
|   |   |   +-- ChatMessage.cs
|   |   |   +-- ChatMessageFormatter.cs  # Format messages + reasoning
|   |   +-- Routing/
|   |   |   +-- CliChatMessageRouter.cs  # Input queue, hash-based IDs, command handling
|   |   |   +-- CliCommandHandler.cs     # /clear, /cancel CLI commands
|   |   +-- Ui/
|   |       +-- ApprovalDialog.cs        # Tool approval modal dialog
|   |       +-- ChatListDataSource.cs    # Scrollable message list
|   |       +-- CliUiFactory.cs
|   |       +-- CollapseStateManager.cs  # Collapsible tool call sections
|   |       +-- TerminalGuiAdapter.cs    # ITerminalSession implementation
|   |       +-- ThinkingIndicator.cs     # Animated thinking spinner
|   +-- Clients/              # External service clients
|   |   +-- BraveSearchClient.cs         # IWebSearchClient via Brave Search API
|   |   +-- IdealistaClient.cs           # IIdealistaClient for real estate
|   |   +-- LocalFileSystemClient.cs     # IFileSystemClient for local disk
|   |   +-- Browser/                     # Playwright web browser
|   |   |   +-- BrowserSessionManager.cs
|   |   |   +-- CapSolverClient.cs       # ICaptchaSolver via CapSolver
|   |   |   +-- ModalDismisser.cs
|   |   |   +-- PlaywrightWebBrowser.cs  # IWebBrowser implementation
|   |   +-- Messaging/                   # Chat messenger clients
|   |   |   +-- CompositeChatMessengerClient.cs  # Multiplexes multiple clients with routing
|   |   |   +-- Cli/
|   |   |   |   +-- CliChatMessengerClient.cs     # IChatMessengerClient for Terminal.Gui
|   |   |   |   +-- OneShotChatMessengerClient.cs # IChatMessengerClient for single prompt
|   |   |   +-- ServiceBus/
|   |   |   |   +-- ServiceBusChatMessengerClient.cs  # IChatMessengerClient for Azure SB
|   |   |   |   +-- ServiceBusMessageParser.cs        # Validates incoming SB messages
|   |   |   |   +-- ServiceBusProcessorHost.cs        # BackgroundService for SB processor
|   |   |   |   +-- ServiceBusPromptReceiver.cs       # Maps correlation -> topic, queues prompt
|   |   |   |   +-- ServiceBusResponseHandler.cs      # Filters + sends responses back to SB
|   |   |   |   +-- ServiceBusResponseWriter.cs       # Writes to SB response queue with retry
|   |   |   |   +-- ServiceBusSourceMapper.cs         # Redis-backed correlation ID mapping
|   |   |   +-- Telegram/
|   |   |   |   +-- TelegramBotHelper.cs
|   |   |   |   +-- TelegramChatClient.cs    # IChatMessengerClient for Telegram
|   |   |   +-- WebChat/
|   |   |       +-- ApprovalContext.cs
|   |   |       +-- BroadcastChannel.cs       # Per-topic broadcast for stream subscribers
|   |   |       +-- HubNotifier.cs            # INotifier via IHubNotificationSender
|   |   |       +-- StreamBuffer.cs           # Buffered stream content for reconnection
|   |   |       +-- WebChatApprovalManager.cs # Pending approval tracking
|   |   |       +-- WebChatMessengerClient.cs # IChatMessengerClient for WebChat
|   |   |       +-- WebChatSession.cs         # Per-topic session data
|   |   |       +-- WebChatSessionManager.cs  # Topic-to-session registry
|   |   |       +-- WebChatStreamManager.cs   # Stream buffer management
|   |   +-- ToolApproval/                # IToolApprovalHandler implementations
|   |   |   +-- AutoToolApprovalHandler.cs       # Always approve (OneShot mode)
|   |   |   +-- CliToolApprovalHandler.cs        # Terminal.Gui dialog
|   |   |   +-- TelegramToolApprovalHandler.cs   # Telegram inline keyboard
|   |   |   +-- WebToolApprovalHandler.cs        # SignalR notification
|   |   +-- Torrent/                     # Download clients
|   |       +-- JackettSearchClient.cs   # ISearchClient via Jackett
|   |       +-- QBittorrentDownloadClient.cs  # IDownloadClient via qBittorrent
|   +-- CommandRunners/       # Shell command execution
|   |   +-- AvailableShell.cs        # IAvailableShell implementation
|   |   +-- BaseCliRunner.cs         # Abstract base for CLI runners
|   |   +-- BashRunner.cs
|   |   +-- CmdRunner.cs
|   |   +-- CommandRunnerFactory.cs  # ICommandRunner factory
|   |   +-- PowerShellRunner.cs
|   |   +-- ShRunner.cs
|   +-- Extensions/           # Infrastructure extensions
|   |   +-- DdnsIpAllowlistExtensions.cs  # Middleware registration
|   |   +-- HttpClientBuilderExtensions.cs  # Polly retry policies
|   |   +-- McpServerExtensions.cs    # MCP server DI helpers
|   |   +-- TaskExtensions.cs
|   +-- HtmlProcessing/       # HTML content processing
|   |   +-- HtmlConverter.cs   # HTML -> Markdown conversion
|   |   +-- HtmlInspector.cs   # Page structure inspection
|   |   +-- HtmlProcessor.cs   # Content extraction
|   +-- Memory/               # Vector memory
|   |   +-- OpenRouterEmbeddingService.cs  # IEmbeddingService via OpenRouter
|   |   +-- RedisStackMemoryStore.cs       # IMemoryStore via Redis Stack + HNSW
|   +-- Middleware/            # HTTP middleware
|   |   +-- DdnsIpAllowlistMiddleware.cs   # IP allowlist via DDNS resolution
|   +-- StateManagers/        # State persistence
|   |   +-- RedisScheduleStore.cs          # IScheduleStore via Redis
|   |   +-- RedisThreadStateStore.cs       # IThreadStateStore via Redis
|   |   +-- SearchResultsManager.cs        # ISearchResultsManager
|   |   +-- TrackedDownloadsManager.cs     # ITrackedDownloadsManager
|   +-- Utils/                # Utilities
|   |   +-- ToolPatternMatcher.cs    # Glob pattern matching for tool whitelists
|   |   +-- ToolResponse.cs          # Standardized tool response formatting
|   |   +-- TopicIdHasher.cs         # Deterministic topic/chat/thread ID generation
|   +-- Validation/           # Validators
|       +-- CronValidator.cs         # ICronValidator implementation
|
+-- McpServerLibrary/         # MCP server: media library management
|   +-- McpTools/
|   |   +-- McpCleanupDownloadTool.cs
|   |   +-- McpContentRecommendationTool.cs
|   |   +-- McpFileDownloadTool.cs
|   |   +-- McpFileSearchTool.cs
|   |   +-- McpGetDownloadStatusTool.cs
|   |   +-- McpGlobFilesTool.cs
|   |   +-- McpMoveTool.cs
|   |   +-- McpResubscribeDownloadsTool.cs
|   +-- McpPrompts/
|   |   +-- McpSystemPrompt.cs
|   +-- McpResources/
|   |   +-- McpDownloadResource.cs
|   +-- ResourceSubscriptions/
|   |   +-- SubscriptionHandlers.cs
|   |   +-- SubscriptionMonitor.cs   # BackgroundService: monitors downloads, sends notifications
|   |   +-- SubscriptionTracker.cs
|   +-- Modules/
|   |   +-- ConfigModule.cs
|   |   +-- InjectorModule.cs        # Jackett, qBittorrent, FileSystem DI
|   +-- Settings/
|   |   +-- McpSettings.cs
|   +-- Program.cs
|
+-- McpServerText/            # MCP server: text/file operations
|   +-- McpTools/
|   |   +-- McpMoveTool.cs
|   |   +-- McpRemoveTool.cs
|   |   +-- McpTextCreateTool.cs
|   |   +-- McpTextEditTool.cs
|   |   +-- McpTextGlobFilesTool.cs
|   |   +-- McpTextReadTool.cs
|   |   +-- McpTextSearchTool.cs
|   +-- McpPrompts/
|   |   +-- McpSystemPrompt.cs
|   +-- Modules/
|   |   +-- ConfigModule.cs
|   +-- Settings/
|   |   +-- McpSettings.cs
|   +-- Program.cs
|
+-- McpServerWebSearch/       # MCP server: web browsing
|   +-- McpTools/
|   |   +-- McpWebBrowseTool.cs
|   |   +-- McpWebClickTool.cs
|   |   +-- McpWebInspectTool.cs
|   |   +-- McpWebSearchTool.cs
|   +-- McpPrompts/
|   |   +-- McpSystemPrompt.cs
|   +-- Modules/
|   |   +-- ConfigModule.cs
|   +-- Settings/
|   |   +-- McpSettings.cs
|   +-- Program.cs
|
+-- McpServerMemory/          # MCP server: vector memory
|   +-- McpTools/
|   |   +-- McpMemoryForgetTool.cs
|   |   +-- McpMemoryListTool.cs
|   |   +-- McpMemoryRecallTool.cs
|   |   +-- McpMemoryReflectTool.cs
|   |   +-- McpMemoryStoreTool.cs
|   +-- McpPrompts/
|   |   +-- McpSystemPrompt.cs
|   +-- Modules/
|   |   +-- ConfigModule.cs
|   +-- Settings/
|   |   +-- McpSettings.cs
|   +-- Program.cs
|
+-- McpServerIdealista/       # MCP server: real estate search
|   +-- McpTools/
|   |   +-- McpPropertySearchTool.cs
|   +-- McpPrompts/
|   |   +-- McpSystemPrompt.cs
|   +-- Modules/
|   |   +-- ConfigModule.cs
|   +-- Settings/
|   |   +-- McpSettings.cs
|   +-- Program.cs
|
+-- McpServerCommandRunner/   # MCP server: shell command execution
|   +-- McpTools/
|   |   +-- McpGetCliPlatformTool.cs
|   |   +-- McpRunCommandTool.cs
|   +-- Modules/
|   |   +-- ConfigModule.cs
|   +-- Settings/
|   |   +-- McpSettings.cs
|   +-- Program.cs
|
+-- WebChat/                  # Blazor server host
|   +-- Program.cs            # Hosts WebAssembly client
|
+-- WebChat.Client/           # Blazor WebAssembly client
|   +-- Components/           # Razor components
|   |   +-- AgentSelector.razor
|   |   +-- ApprovalModal.razor
|   |   +-- AvatarImage.razor
|   |   +-- ChatInput.razor
|   |   +-- ChatMessage.razor
|   |   +-- TopicList.razor
|   |   +-- UserIdentityPicker.razor
|   |   +-- Chat/
|   |   |   +-- ChatContainer.razor
|   |   |   +-- ConnectionStatus.razor
|   |   |   +-- EmptyState.razor
|   |   |   +-- MessageList.razor
|   |   |   +-- StreamingMessageDisplay.razor
|   |   |   +-- SuggestionChips.razor
|   |   +-- Toast/
|   |       +-- ToastContainer.razor
|   |       +-- ToastItem.razor
|   +-- Contracts/            # Service interfaces
|   |   +-- IAgentService.cs
|   |   +-- IApprovalService.cs
|   |   +-- IChatConnectionService.cs
|   |   +-- IChatMessagingService.cs
|   |   +-- IChatSessionService.cs
|   |   +-- ILocalStorageService.cs
|   |   +-- ISignalREventSubscriber.cs
|   |   +-- IStreamResumeService.cs
|   |   +-- IStreamingService.cs
|   |   +-- ITopicService.cs
|   +-- Extensions/           # Service extensions
|   |   +-- ChatHistoryMessageExtensions.cs
|   |   +-- ServiceCollectionExtensions.cs
|   +-- Helpers/              # UI helpers
|   |   +-- AvatarHelper.cs
|   |   +-- MessageFieldMerger.cs
|   +-- Layout/
|   |   +-- MainLayout.razor
|   +-- Models/               # Client models
|   |   +-- ChatMessageModel.cs
|   |   +-- StoredTopic.cs
|   |   +-- UserConfig.cs
|   +-- Pages/
|   |   +-- NotFound.razor
|   +-- Services/             # Service implementations
|   |   +-- AgentService.cs
|   |   +-- ApprovalService.cs
|   |   +-- ChatConnectionService.cs
|   |   +-- ChatMessagingService.cs
|   |   +-- ChatSessionService.cs
|   |   +-- ConfigService.cs
|   |   +-- LocalStorageService.cs
|   |   +-- SignalREventSubscriber.cs
|   |   +-- TopicService.cs
|   |   +-- Streaming/
|   |   |   +-- BufferRebuildUtility.cs    # Rebuilds state from stream buffer
|   |   |   +-- StreamResumeService.cs     # Handles stream resumption after reconnect
|   |   |   +-- StreamingService.cs        # Manages active streaming subscriptions
|   |   |   +-- TransientErrorFilter.cs    # Filters transient SignalR errors
|   |   +-- Utilities/
|   |       +-- TopicIdGenerator.cs
|   +-- State/                # Redux-like state management
|   |   +-- Dispatcher.cs           # Central action bus
|   |   +-- IAction.cs              # Action marker interface
|   |   +-- IDispatcher.cs          # Dispatcher interface
|   |   +-- RenderCoordinator.cs    # Throttled observables (50ms) for streaming
|   |   +-- Selector.cs             # Memoized state projections
|   |   +-- Store.cs                # Generic store (BehaviorSubject<TState>)
|   |   +-- StoreSubscriberComponent.cs  # Base component with Rx subscriptions
|   |   +-- Approval/
|   |   |   +-- ApprovalActions.cs
|   |   |   +-- ApprovalReducers.cs
|   |   |   +-- ApprovalState.cs
|   |   |   +-- ApprovalStore.cs
|   |   +-- Connection/
|   |   |   +-- ConnectionActions.cs
|   |   |   +-- ConnectionReducers.cs
|   |   |   +-- ConnectionState.cs
|   |   |   +-- ConnectionStore.cs
|   |   +-- Effects/
|   |   |   +-- AgentSelectionEffect.cs
|   |   |   +-- InitializationEffect.cs
|   |   |   +-- SendMessageEffect.cs
|   |   |   +-- TopicDeleteEffect.cs
|   |   |   +-- TopicSelectionEffect.cs
|   |   |   +-- UserIdentityEffect.cs
|   |   +-- Hub/
|   |   |   +-- ConnectionEventDispatcher.cs  # Hub lifecycle -> connection actions
|   |   |   +-- HubEventDispatcher.cs         # Hub notifications -> store actions
|   |   |   +-- IHubEventDispatcher.cs
|   |   |   +-- ReconnectionEffect.cs
|   |   +-- Messages/
|   |   |   +-- MessagesActions.cs
|   |   |   +-- MessagesReducers.cs
|   |   |   +-- MessagesState.cs
|   |   |   +-- MessagesStore.cs
|   |   +-- Pipeline/
|   |   |   +-- IMessagePipeline.cs
|   |   |   +-- MessagePipeline.cs     # Chunk accumulation, finalization, dedup
|   |   |   +-- PipelineSnapshot.cs
|   |   +-- Streaming/
|   |   |   +-- StreamingActions.cs
|   |   |   +-- StreamingReducers.cs
|   |   |   +-- StreamingSelectors.cs
|   |   |   +-- StreamingState.cs
|   |   |   +-- StreamingStore.cs
|   |   +-- Toast/
|   |   |   +-- ToastActions.cs
|   |   |   +-- ToastState.cs
|   |   |   +-- ToastStore.cs
|   |   +-- Topics/
|   |   |   +-- TopicsActions.cs
|   |   |   +-- TopicsReducers.cs
|   |   |   +-- TopicsState.cs
|   |   |   +-- TopicsStore.cs
|   |   +-- UserIdentity/
|   |       +-- UserIdentityActions.cs
|   |       +-- UserIdentityReducers.cs
|   |       +-- UserIdentityState.cs
|   |       +-- UserIdentityStore.cs
|   +-- App.razor
|   +-- Program.cs
|   +-- _Imports.razor
|
+-- Tests/                    # Test project
|   +-- Integration/          # Integration tests
|   |   +-- Agents/
|   |   |   +-- McpAgentIntegrationTests.cs
|   |   |   +-- McpSamplingHandlerIntegrationTests.cs
|   |   |   +-- McpSubscriptionManagerIntegrationTests.cs
|   |   |   +-- OpenRouterReasoningIntegrationTests.cs
|   |   |   +-- OpenRouterToolCallingWithReasoningIntegrationTests.cs
|   |   |   +-- ThreadSessionIntegrationTests.cs
|   |   |   +-- ToolApprovalChatClientIntegrationTests.cs
|   |   +-- Clients/
|   |   |   +-- BraveSearchClientIntegrationTests.cs
|   |   |   +-- JackettSearchClientTests.cs
|   |   |   +-- OneShotChatMessengerClientTests.cs
|   |   |   +-- PlaywrightWebBrowserIntegrationTests.cs
|   |   |   +-- QBittorrentDownloadClientTests.cs
|   |   |   +-- TelegramBotChatMessengerClientTests.cs
|   |   +-- Domain/
|   |   |   +-- StateManagerTests.cs
|   |   +-- Fixtures/         # Test fixtures
|   |   |   +-- FakeAgentFactory.cs
|   |   |   +-- JackettFixture.cs
|   |   |   +-- McpLibraryServerFixture.cs
|   |   |   +-- PlaywrightWebBrowserFixture.cs
|   |   |   +-- QBittorrentFixture.cs
|   |   |   +-- RedisFixture.cs
|   |   |   +-- ServiceBusFixture.cs
|   |   |   +-- TelegramBotFixture.cs
|   |   |   +-- ThreadSessionServerFixture.cs
|   |   |   +-- WebChatServerFixture.cs
|   |   +-- Infrastructure/
|   |   |   +-- LocalFileSystemClientTests.cs
|   |   +-- Jack/
|   |   |   +-- DependencyInjectionTests.cs
|   |   +-- McpServerTests/
|   |   |   +-- McpLibraryServerTests.cs
|   |   |   +-- SubscriptionMonitorIntegrationTests.cs
|   |   +-- McpTools/
|   |   |   +-- ResubscribeDownloadsToolIntegrationTests.cs
|   |   +-- Memory/
|   |   |   +-- EmbeddingIntegrationTests.cs
|   |   |   +-- RedisMemoryStoreTests.cs
|   |   +-- Messaging/
|   |   |   +-- ServiceBusIntegrationTests.cs
|   |   +-- StateManagers/
|   |   |   +-- RedisScheduleStoreTests.cs
|   |   +-- WebChat/
|   |       +-- ChatHubIntegrationTests.cs
|   |       +-- SessionPersistenceIntegrationTests.cs
|   |       +-- StreamResumeIntegrationTests.cs
|   |       +-- Client/
|   |           +-- Adapters/
|   |           |   +-- HubConnectionApprovalService.cs
|   |           |   +-- HubConnectionMessagingService.cs
|   |           |   +-- HubConnectionTopicService.cs
|   |           +-- ConcurrentStreamingTests.cs
|   |           +-- StreamResumeServiceIntegrationTests.cs
|   |           +-- StreamingServiceIntegrationTests.cs
|   +-- Unit/                 # Unit tests
|       +-- ChatMessageSerializationTests.cs
|       +-- Domain/
|       |   +-- ChatThreadContextTests.cs
|       |   +-- ChatThreadResolverTests.cs
|       |   +-- GroupByStreamingTests.cs
|       |   +-- MergeTests.cs
|       |   +-- MonitorTests.cs
|       |   +-- MoveToolTests.cs
|       |   +-- RemoveToolTests.cs
|       |   +-- ResubscribeDownloadsToolTests.cs
|       |   +-- DTOs/WebChat/ChatStreamMessageTests.cs
|       |   +-- Scheduling/
|       |   |   +-- ScheduleCreateToolTests.cs
|       |   |   +-- ScheduleDtoTests.cs
|       |   |   +-- ScheduleExecutorTests.cs
|       |   |   +-- ScheduleListToolTests.cs
|       |   +-- Text/
|       |       +-- TextCreateToolTests.cs
|       |       +-- TextEditToolTests.cs
|       |       +-- TextReadToolTests.cs
|       |       +-- TextSearchToolTests.cs
|       |       +-- TextToolBaseTests.cs
|       +-- Infrastructure/
|       |   +-- BraveSearchClientTests.cs
|       |   +-- CronValidatorTests.cs
|       |   +-- HtmlInspectorTests.cs
|       |   +-- HtmlProcessorTests.cs
|       |   +-- HubNotifierTests.cs
|       |   +-- OpenRouterHttpHelpersTests.cs
|       |   +-- PlaywrightWebBrowserTests.cs
|       |   +-- RedisChatMessageStoreTests.cs
|       |   +-- TelegramToolApprovalHandlerTests.cs
|       |   +-- ToolApprovalChatClientTests.cs
|       |   +-- ToolPatternMatcherTests.cs
|       |   +-- WebChatStreamManagerTests.cs
|       |   +-- Cli/
|       |   |   +-- CliChatMessageRouterTests.cs
|       |   |   +-- FakeTerminalAdapter.cs
|       |   +-- Memory/
|       |   |   +-- MemoryEntryTests.cs
|       |   |   +-- OpenRouterEmbeddingServiceMockTests.cs
|       |   +-- Messaging/
|       |       +-- CompositeChatMessengerClientTests.cs
|       |       +-- MessageSourceRouterTests.cs
|       |       +-- MessageSourceRoutingTests.cs
|       |       +-- ServiceBusChatMessengerClientTests.cs
|       |       +-- ServiceBusMessageParserTests.cs
|       |       +-- ServiceBusPromptReceiverTests.cs
|       |       +-- ServiceBusResponseHandlerTests.cs
|       |       +-- ServiceBusResponseWriterTests.cs
|       |       +-- ServiceBusSourceMapperTests.cs
|       |       +-- WebChatMessengerClientTests.cs
|       +-- McpServerLibrary/
|       |   +-- SubscriptionTrackerTests.cs
|       +-- Tools/
|       |   +-- GlobFilesToolTests.cs
|       +-- WebChat/
|       |   +-- Client/
|       |   |   +-- BufferRebuildUtilityTests.cs
|       |   |   +-- ChatHistoryMessageExtensionsTests.cs
|       |   |   +-- ChatMessageModelTests.cs
|       |   |   +-- MessageMergeTests.cs
|       |   |   +-- MessagesReducersTests.cs
|       |   |   +-- StreamResumeServiceTests.cs
|       |   |   +-- StreamingServiceTests.cs
|       |   |   +-- ToastStoreTests.cs
|       |   |   +-- TransientErrorFilterTests.cs
|       |   +-- Fixtures/
|       |       +-- FakeApprovalService.cs
|       |       +-- FakeChatMessagingService.cs
|       |       +-- FakeTopicService.cs
|       +-- WebChat.Client/
|           +-- Services/
|           |   +-- SignalREventSubscriberTests.cs
|           +-- State/
|               +-- ApprovalStoreTests.cs
|               +-- ConnectionEventDispatcherTests.cs
|               +-- ConnectionStoreTests.cs
|               +-- HubEventDispatcherTests.cs
|               +-- MessagesStoreTests.cs
|               +-- ReconnectionEffectTests.cs
|               +-- RenderCoordinatorTests.cs
|               +-- SelectorTests.cs
|               +-- StreamingStoreTests.cs
|               +-- TopicDeleteEffectTests.cs
|               +-- TopicsStoreTests.cs
|               +-- Pipeline/
|                   +-- MessagePipelineIntegrationTests.cs
|                   +-- MessagePipelineTests.cs
|
+-- DockerCompose/            # Docker configuration
|   +-- docker-compose.yml
|   +-- docker-compose.override.yml
|   +-- Caddyfile             # Caddy reverse proxy config
|   +-- .env                  # Environment variables
|   +-- volumes/              # Persistent data volumes
|
+-- docs/                     # Documentation
|   +-- codebase/             # Generated codebase docs
|   +-- plans/                # Implementation plans
|   +-- specs/                # Design specifications
|
+-- .claude/                  # Claude Code configuration
    +-- CLAUDE.md             # Project instructions
    +-- rules/                # Coding rules
```

## File Naming Conventions

| Pattern | Location | Example |
|---------|----------|---------|
| `I*.cs` | Domain/Contracts | `IMemoryStore.cs` |
| `*Tool.cs` | Domain/Tools | `FileDownloadTool.cs` |
| `*ToolFeature.cs` | Domain/Tools | `SchedulingToolFeature.cs` |
| `Mcp*Tool.cs` | McpServer*/McpTools | `McpFileDownloadTool.cs` |
| `*Store.cs` | Infrastructure/StateManagers, WebChat.Client/State | `RedisThreadStateStore.cs`, `MessagesStore.cs` |
| `*Client.cs` | Infrastructure/Clients | `TelegramChatClient.cs`, `OpenRouterChatClient.cs` |
| `*Handler.cs` | Infrastructure/Clients | `WebToolApprovalHandler.cs`, `McpSamplingHandler.cs` |
| `*Writer.cs` | Infrastructure/Clients | `ServiceBusResponseWriter.cs` |
| `*Mapper.cs` | Infrastructure/Clients | `ServiceBusConversationMapper.cs` |
| `*Parser.cs` | Infrastructure/Clients | `ServiceBusMessageParser.cs` |
| `*Receiver.cs` | Infrastructure/Clients | `ServiceBusPromptReceiver.cs` |
| `*Host.cs` | Infrastructure/Clients | `ServiceBusProcessorHost.cs` |
| `*Router.cs` | Domain/Routers, Infrastructure/CliGui | `MessageSourceRouter.cs`, `CliChatMessageRouter.cs` |
| `*Monitor.cs` | Domain/Monitor | `ChatMonitor.cs` |
| `*Monitoring.cs` | Agent/App | `ChatMonitoring.cs` (BackgroundService wrapper) |
| `*Dispatcher.cs` | Domain/Monitor, WebChat.Client/State | `ScheduleDispatcher.cs`, `HubEventDispatcher.cs` |
| `*Executor.cs` | Domain/Monitor | `ScheduleExecutor.cs` |
| `*Effect.cs` | WebChat.Client/State/Effects | `TopicDeleteEffect.cs` |
| `*Pipeline.cs` | WebChat.Client/State/Pipeline | `MessagePipeline.cs` |
| `*Reducers.cs` | WebChat.Client/State | `MessagesReducers.cs` |
| `*Actions.cs` | WebChat.Client/State | `MessagesActions.cs` |
| `*State.cs` | WebChat.Client/State | `MessagesState.cs` |
| `*Selectors.cs` | WebChat.Client/State | `StreamingSelectors.cs` |
| `*Extensions.cs` | Domain/Extensions, Infrastructure/Extensions | `ChatMessageExtensions.cs` |
| `*Config.cs` | Domain/Tools/Config | `DownloadPathConfig.cs` |
| `*Module.cs` | Agent/Modules, McpServer*/Modules | `InjectorModule.cs` |
| `*Settings.cs` | Agent/Settings, McpServer*/Settings | `AgentSettings.cs`, `McpSettings.cs` |
| `*Tests.cs` | Tests | `McpAgentIntegrationTests.cs` |
| `*Fixture.cs` | Tests/Integration/Fixtures | `RedisFixture.cs` |

## Module Boundaries

### Domain (Pure)
- No external dependencies
- Interfaces for services Domain needs (in `Contracts/`)
- DTOs as records (in `DTOs/`)
- Business logic in Tool classes (in `Tools/`)
- System prompts (in `Prompts/`)
- Agent abstractions and thread management (in `Agents/`)
- Monitoring and scheduling logic (in `Monitor/`)

### Infrastructure (Implementations)
- Implements Domain interfaces
- External client wrappers (Telegram, Playwright, qBittorrent, Jackett, Brave, Idealista)
- Agent implementation (McpAgent, ThreadSession, MCP integration)
- LLM client (OpenRouterChatClient via OpenAI SDK)
- State persistence (Redis-backed stores)
- CLI GUI (Terminal.Gui components)
- Cannot import from Agent namespace

### Agent (Composition)
- DI registration (Modules/)
- Entry point (Program.cs)
- Background services (ChatMonitoring, ScheduleMonitoring)
- SignalR hub (ChatHub)
- Configuration classes (Settings/)
- Imports from all layers

### MCP Servers (Independent)
- Each server is a separate ASP.NET process
- Wraps Domain tools with `[McpServerTool]` attributes
- Shares Infrastructure for external clients (Jackett, qBittorrent, etc.)
- Each has its own Modules/, Settings/, and Program.cs
- Communicates with Agent via HTTP SSE (MCP protocol)

### WebChat (Host + Client)
- **WebChat**: Minimal Blazor server host
- **WebChat.Client**: Blazor WebAssembly application
  - Redux-like state management (Store + Dispatcher + Effects)
  - Service layer for SignalR communication
  - Razor components for UI
  - No direct dependency on Domain (uses shared DTOs via project reference)
