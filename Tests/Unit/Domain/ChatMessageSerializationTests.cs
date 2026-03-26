using System.Text.Json;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain;

public class ChatMessageSerializationTests
{
    [Fact]
    public void SetAndGetSenderId_StoresAndRetrievesValue()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");

        msg.SetSenderId("Alice");

        msg.AdditionalProperties.ShouldNotBeNull();
        msg.AdditionalProperties["SenderId"].ShouldBe("Alice");
        msg.GetSenderId().ShouldBe("Alice");
    }

    [Fact]
    public void GetSenderId_ReturnsValueAfterJsonRoundtrip()
    {
        // Arrange - simulate what happens when ChatMessage is stored in Redis
        var msg = new ChatMessage(ChatRole.User, "Hello");
        msg.SetSenderId("Alice");

        // Act - serialize and deserialize (simulates Redis storage)
        var json = JsonSerializer.Serialize(msg);
        var deserialized = JsonSerializer.Deserialize<ChatMessage>(json);

        // Assert - GetSenderId should handle JsonElement correctly
        deserialized.ShouldNotBeNull();
        var senderId = deserialized.GetSenderId();
        senderId.ShouldBe("Alice");
    }

    [Fact]
    public void SetAndGetTimestamp_StoresAndRetrievesValue()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");
        var timestamp = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.FromHours(2));

        msg.SetTimestamp(timestamp);

        msg.AdditionalProperties.ShouldNotBeNull();
        msg.AdditionalProperties["Timestamp"].ShouldBe(timestamp);
        msg.GetTimestamp().ShouldBe(timestamp);
    }

    [Fact]
    public void GetTimestamp_ReturnsValueAfterJsonRoundtrip()
    {
        // Arrange - simulate what happens when ChatMessage is stored in Redis
        var msg = new ChatMessage(ChatRole.User, "Hello");
        var timestamp = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.FromHours(2));
        msg.SetTimestamp(timestamp);

        // Act - serialize and deserialize (simulates Redis storage)
        var json = JsonSerializer.Serialize(msg);
        var deserialized = JsonSerializer.Deserialize<ChatMessage>(json);

        // Assert - GetTimestamp should handle JsonElement correctly
        deserialized.ShouldNotBeNull();
        var result = deserialized.GetTimestamp();
        result.ShouldBe(timestamp);
    }

    [Fact]
    public void SetSenderIdOrTimestamp_DoesNothingWhenNull()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");

        msg.SetSenderId(null);
        msg.SetTimestamp(null);

        msg.AdditionalProperties.ShouldBeNull();
    }
}
