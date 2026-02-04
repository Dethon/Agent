using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusMessageParserTests
{
    private readonly ServiceBusMessageParser _parser = new("default-agent");

    [Fact]
    public void Parse_ValidMessage_ReturnsParseSuccess()
    {
        // Arrange
        var message = CreateMessage(
            body: """{"prompt": "Hello", "sender": "user1"}""",
            sourceId: "source-123",
            agentId: "agent-456");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseSuccess>();
        var success = (ParseSuccess)result;
        success.Message.Prompt.ShouldBe("Hello");
        success.Message.Sender.ShouldBe("user1");
        success.Message.SourceId.ShouldBe("source-123");
        success.Message.AgentId.ShouldBe("agent-456");
    }

    [Fact]
    public void Parse_MissingPrompt_ReturnsParseFailure()
    {
        // Arrange
        var message = CreateMessage(
            body: """{"sender": "user1"}""",
            sourceId: "source-123");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MalformedMessage");
        failure.Details.ShouldContain("prompt");
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsParseFailure()
    {
        // Arrange
        var message = CreateMessage(body: "not json", sourceId: "source-123");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("DeserializationError");
    }

    [Fact]
    public void Parse_MissingSourceId_GeneratesNewSourceId()
    {
        // Arrange
        var message = CreateMessage(
            body: """{"prompt": "Hello", "sender": "user1"}""",
            sourceId: null);

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseSuccess>();
        var success = (ParseSuccess)result;
        success.Message.SourceId.ShouldNotBeNullOrEmpty();
        success.Message.SourceId.Length.ShouldBe(32); // GUID without dashes
    }

    [Fact]
    public void Parse_MissingAgentId_UsesDefaultAgentId()
    {
        // Arrange
        var message = CreateMessage(
            body: """{"prompt": "Hello", "sender": "user1"}""",
            sourceId: "source-123",
            agentId: null);

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseSuccess>();
        var success = (ParseSuccess)result;
        success.Message.AgentId.ShouldBe("default-agent");
    }

    [Fact]
    public void Parse_EmptyPrompt_ReturnsParseFailure()
    {
        // Arrange
        var message = CreateMessage(
            body: """{"prompt": "", "sender": "user1"}""",
            sourceId: "source-123");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MalformedMessage");
    }

    private static ServiceBusReceivedMessage CreateMessage(string body, string? sourceId, string? agentId = null)
    {
        var props = new Dictionary<string, object>();
        if (sourceId is not null)
            props["sourceId"] = sourceId;
        if (agentId is not null)
            props["agentId"] = agentId;

        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(body),
            messageId: Guid.NewGuid().ToString(),
            applicationProperties: props);
    }
}
