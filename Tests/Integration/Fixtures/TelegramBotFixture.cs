using System.Text.Json;
using Infrastructure.Clients;
using Telegram.Bot;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Integration.Fixtures;

public class TelegramBotFixture : IAsyncLifetime
{
    private WireMockServer _server = null!;
    private const string TestBotToken = "123456789:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";

    private string BaseUrl { get; set; } = null!;
    private static string BotToken => TestBotToken;
    public string[] AllowedUserNames { get; } = ["testuser", "alloweduser"];

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        BaseUrl = _server.Url!;

        // Setup default getMe response (required by TelegramBotClient)
        SetupGetMe();

        return Task.CompletedTask;
    }

    public TelegramBotChatMessengerClient CreateClient()
    {
        var options = new TelegramBotClientOptions(BotToken, BaseUrl);
        var botClient = new TelegramBotClient(options);
        return new TelegramBotChatMessengerClient(botClient, AllowedUserNames);
    }

    private void SetupGetMe()
    {
        var response = new
        {
            ok = true,
            result = new
            {
                id = 123456789,
                is_bot = true,
                first_name = "TestBot",
                username = "test_bot",
                can_join_groups = true,
                can_read_all_group_messages = false,
                supports_inline_queries = false
            }
        };

        _server.Given(
                Request.Create()
                    .WithPath($"/bot{BotToken}/getMe")
                    .UsingAnyMethod())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(JsonSerializer.Serialize(response)));
    }

    public void SetupGetUpdates(object[] updates)
    {
        var response = new
        {
            ok = true,
            result = updates
        };

        var requestBuilder = Request.Create()
            .WithPath($"/bot{BotToken}/getUpdates")
            .UsingAnyMethod();

        _server.Given(requestBuilder)
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(JsonSerializer.Serialize(response)));
    }

    public void SetupGetUpdatesSequence(params object[][] updatesSequence)
    {
        // For simplicity, just return the first batch of updates once, then empty
        // The test should break out after getting the expected prompts
        if (updatesSequence.Length == 0) return;

        // Return the first batch, then empty arrays for subsequent calls
        var firstBatch = updatesSequence[0];
        var response = new
        {
            ok = true,
            result = firstBatch
        };

        _server.Given(
                Request.Create()
                    .WithPath($"/bot{BotToken}/getUpdates")
                    .UsingAnyMethod())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(JsonSerializer.Serialize(response)));
    }

    public void SetupSendMessage(long chatId)
    {
        var response = new
        {
            ok = true,
            result = new
            {
                message_id = Random.Shared.Next(1, 1000000),
                from = new
                {
                    id = 123456789,
                    is_bot = true,
                    first_name = "TestBot",
                    username = "test_bot"
                },
                chat = new
                {
                    id = chatId,
                    type = "private"
                },
                date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                text = "Response"
            }
        };

        _server.Given(
                Request.Create()
                    .WithPath($"/bot{BotToken}/sendMessage")
                    .UsingAnyMethod())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(JsonSerializer.Serialize(response)));
    }

    public void SetupCreateForumTopic(long chatId, int threadId)
    {
        var response = new
        {
            ok = true,
            result = new
            {
                message_thread_id = threadId,
                name = "Test Topic",
                icon_color = 0x6FB9F0
            }
        };

        _server.Given(
                Request.Create()
                    .WithPath($"/bot{BotToken}/createForumTopic")
                    .UsingAnyMethod())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(JsonSerializer.Serialize(response)));
    }

    public void SetupEditForumTopic(bool exists)
    {
        if (exists)
        {
            var response = new
            {
                ok = true,
                result = true
            };
            _server.Given(
                    Request.Create()
                        .WithPath($"/bot{BotToken}/editForumTopic")
                        .UsingAnyMethod())
                .RespondWith(
                    Response.Create()
                        .WithStatusCode(200)
                        .WithHeader("Content-Type", "application/json")
                        .WithBody(JsonSerializer.Serialize(response)));
        }
        else
        {
            var response = new
            {
                ok = false,
                error_code = 400,
                description = "Bad Request: TOPIC_ID_INVALID"
            };
            _server.Given(
                    Request.Create()
                        .WithPath($"/bot{BotToken}/editForumTopic")
                        .UsingAnyMethod())
                .RespondWith(
                    Response.Create()
                        .WithStatusCode(400)
                        .WithHeader("Content-Type", "application/json")
                        .WithBody(JsonSerializer.Serialize(response)));
        }
    }

    public void SetupGetForumTopicIconStickers()
    {
        var response = new
        {
            ok = true,
            result = new[]
            {
                new
                {
                    width = 100,
                    height = 100,
                    emoji = "🏴‍☠️",
                    set_name = "TopicIcons",
                    is_animated = false,
                    is_video = false,
                    type = "custom_emoji",
                    custom_emoji_id = "5368324170671202286",
                    file_id = "test_file_id",
                    file_unique_id = "test_unique_id"
                }
            }
        };

        _server.Given(
                Request.Create()
                    .WithPath($"/bot{BotToken}/getForumTopicIconStickers")
                    .UsingAnyMethod())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(JsonSerializer.Serialize(response)));
    }

    public static object CreateTextMessageUpdate(
        int updateId,
        long chatId,
        string text,
        string username,
        int? messageThreadId = null)
    {
        var message = new Dictionary<string, object>
        {
            ["message_id"] = updateId * 10,
            ["from"] = new
            {
                id = chatId,
                is_bot = false,
                first_name = "Test",
                username
            },
            ["chat"] = new
            {
                id = chatId,
                type = messageThreadId.HasValue ? "supergroup" : "private",
                username
            },
            ["date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["text"] = text
        };

        if (messageThreadId.HasValue)
        {
            message["message_thread_id"] = messageThreadId.Value;
        }

        return new
        {
            update_id = updateId,
            message
        };
    }

    public void Reset()
    {
        _server.Reset();
        SetupGetMe();
    }

    public Task DisposeAsync()
    {
        _server.Stop();
        _server.Dispose();
        return Task.CompletedTask;
    }
}