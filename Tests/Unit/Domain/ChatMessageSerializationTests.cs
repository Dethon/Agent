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

    [Fact]
    public void SetAndGetLocation_StoresAndRetrievesValue()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");

        msg.SetLocation("the office");

        msg.AdditionalProperties.ShouldNotBeNull();
        msg.AdditionalProperties["Location"].ShouldBe("the office");
        msg.GetLocation().ShouldBe("the office");
    }

    [Fact]
    public void GetLocation_ReturnsValueAfterJsonRoundtrip()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");
        msg.SetLocation("the office");

        var json = JsonSerializer.Serialize(msg);
        var deserialized = JsonSerializer.Deserialize<ChatMessage>(json);

        deserialized.ShouldNotBeNull();
        deserialized.GetLocation().ShouldBe("the office");
    }

    [Fact]
    public void SetAndGetSatelliteId_StoresAndRetrievesValue()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");

        msg.SetSatelliteId("kitchen-01");

        msg.AdditionalProperties.ShouldNotBeNull();
        msg.AdditionalProperties["SatelliteId"].ShouldBe("kitchen-01");
        msg.GetSatelliteId().ShouldBe("kitchen-01");
    }

    [Fact]
    public void GetSatelliteId_ReturnsValueAfterJsonRoundtrip()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");
        msg.SetSatelliteId("kitchen-01");

        var json = JsonSerializer.Serialize(msg);
        var deserialized = JsonSerializer.Deserialize<ChatMessage>(json);

        deserialized.ShouldNotBeNull();
        deserialized.GetSatelliteId().ShouldBe("kitchen-01");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void SetSatelliteId_DoesNothingWhenNullOrWhitespace(string? satelliteId)
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");

        msg.SetSatelliteId(satelliteId);

        msg.AdditionalProperties.ShouldBeNull();
    }
}