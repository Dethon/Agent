using System.Text.Json;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit;

public class ChatMessageSerializationTests
{
    [Fact]
    public void SetSenderId_StoresValueInAdditionalProperties()
    {
        // Arrange
        var msg = new ChatMessage(ChatRole.User, "Hello");

        // Act
        msg.SetSenderId("Alice");

        // Assert
        msg.AdditionalProperties.ShouldNotBeNull();
        msg.AdditionalProperties["SenderId"].ShouldBe("Alice");
    }

    [Fact]
    public void GetSenderId_ReturnsStringValue()
    {
        // Arrange
        var msg = new ChatMessage(ChatRole.User, "Hello")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["SenderId"] = "Alice"
            }
        };

        // Act
        var senderId = msg.GetSenderId();

        // Assert
        senderId.ShouldBe("Alice");
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
    public void GetSenderId_ReturnsNullWhenNotSet()
    {
        // Arrange
        var msg = new ChatMessage(ChatRole.User, "Hello");

        // Act
        var senderId = msg.GetSenderId();

        // Assert
        senderId.ShouldBeNull();
    }

    [Fact]
    public void SetSenderId_DoesNothingWhenNull()
    {
        // Arrange
        var msg = new ChatMessage(ChatRole.User, "Hello");

        // Act
        msg.SetSenderId(null);

        // Assert
        msg.AdditionalProperties.ShouldBeNull();
    }

    [Fact]
    public void SetTimestamp_StoresValueInAdditionalProperties()
    {
        // Arrange
        var msg = new ChatMessage(ChatRole.User, "Hello");
        var timestamp = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.FromHours(2));

        // Act
        msg.SetTimestamp(timestamp);

        // Assert
        msg.AdditionalProperties.ShouldNotBeNull();
        msg.AdditionalProperties["Timestamp"].ShouldBe(timestamp);
    }

    [Fact]
    public void GetTimestamp_ReturnsDateTimeOffsetValue()
    {
        // Arrange
        var timestamp = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.FromHours(2));
        var msg = new ChatMessage(ChatRole.User, "Hello")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["Timestamp"] = timestamp
            }
        };

        // Act
        var result = msg.GetTimestamp();

        // Assert
        result.ShouldBe(timestamp);
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
    public void GetTimestamp_ReturnsNullWhenNotSet()
    {
        // Arrange
        var msg = new ChatMessage(ChatRole.User, "Hello");

        // Act
        var result = msg.GetTimestamp();

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void SetTimestamp_DoesNothingWhenNull()
    {
        // Arrange
        var msg = new ChatMessage(ChatRole.User, "Hello");

        // Act
        msg.SetTimestamp(null);

        // Assert
        msg.AdditionalProperties.ShouldBeNull();
    }
}