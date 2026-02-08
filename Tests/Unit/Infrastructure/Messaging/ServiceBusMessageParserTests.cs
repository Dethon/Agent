using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using Infrastructure.Clients.Messaging.ServiceBus;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public class ServiceBusMessageParserTests
{
    private readonly ServiceBusMessageParser _parser = new(["agent-456", "default-agent", "test-agent"]);

    [Fact]
    public void Parse_ValidMessage_ReturnsParseSuccess()
    {
        // Arrange
        var message = CreateMessage(
            correlationId: "correlation-123",
            agentId: "agent-456",
            prompt: "Hello",
            sender: "user1");

        // Act
        var result = _parser.Parse(message);

        // Assert
        result.ShouldBeOfType<ParseSuccess>();
        var success = (ParseSuccess)result;
        success.Message.Prompt.ShouldBe("Hello");
        success.Message.Sender.ShouldBe("user1");
        success.Message.CorrelationId.ShouldBe("correlation-123");
        success.Message.AgentId.ShouldBe("agent-456");
    }

    [Fact]
    public void Parse_MissingPrompt_ReturnsParseFailure()
    {
        var message = CreateMessage(correlationId: "correlation-123", agentId: "agent-456", prompt: null,
            sender: "user1");
        var result = _parser.Parse(message);
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MissingField");
        failure.Details.ShouldContain("prompt");
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsParseFailure()
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("not json"),
            messageId: Guid.NewGuid().ToString());
        var result = _parser.Parse(message);
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("DeserializationError");
    }

    [Fact]
    public void Parse_MissingCorrelationId_ReturnsParseFailure()
    {
        var message = CreateMessage(correlationId: null, agentId: "agent-456", prompt: "Hello", sender: "user1");
        var result = _parser.Parse(message);
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MissingField");
        failure.Details.ShouldContain("correlationId");
    }

    [Fact]
    public void Parse_MissingAgentId_ReturnsParseFailure()
    {
        var message = CreateMessage(correlationId: "correlation-123", agentId: null, prompt: "Hello", sender: "user1");
        var result = _parser.Parse(message);
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MissingField");
        failure.Details.ShouldContain("agentId");
    }

    [Fact]
    public void Parse_InvalidAgentId_ReturnsParseFailure()
    {
        var message = CreateMessage(correlationId: "correlation-123", agentId: "unknown-agent", prompt: "Hello",
            sender: "user1");
        var result = _parser.Parse(message);
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("InvalidAgentId");
        failure.Details.ShouldContain("unknown-agent");
    }

    [Fact]
    public void Parse_EmptyPrompt_ReturnsParseFailure()
    {
        var message = CreateMessage(correlationId: "correlation-123", agentId: "agent-456", prompt: "",
            sender: "user1");
        var result = _parser.Parse(message);
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MissingField");
    }

    [Fact]
    public void Parse_EmptyCorrelationId_ReturnsParseFailure()
    {
        var message = CreateMessage(correlationId: "", agentId: "agent-456", prompt: "Hello", sender: "user1");
        var result = _parser.Parse(message);
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MissingField");
        failure.Details.ShouldContain("correlationId");
    }

    [Fact]
    public void Parse_EmptyAgentId_ReturnsParseFailure()
    {
        var message = CreateMessage(correlationId: "correlation-123", agentId: "", prompt: "Hello", sender: "user1");
        var result = _parser.Parse(message);
        result.ShouldBeOfType<ParseFailure>();
        var failure = (ParseFailure)result;
        failure.Reason.ShouldBe("MissingField");
        failure.Details.ShouldContain("agentId");
    }

    private static ServiceBusReceivedMessage CreateMessage(string? correlationId, string? agentId, string? prompt,
        string? sender)
    {
        var bodyObj = new Dictionary<string, object?>();
        if (correlationId is not null)
        {
            bodyObj["correlationId"] = correlationId;
        }

        if (agentId is not null)
        {
            bodyObj["agentId"] = agentId;
        }

        if (prompt is not null)
        {
            bodyObj["prompt"] = prompt;
        }

        if (sender is not null)
        {
            bodyObj["sender"] = sender;
        }

        var json = JsonSerializer.Serialize(bodyObj);
        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(json),
            messageId: Guid.NewGuid().ToString());
    }
}