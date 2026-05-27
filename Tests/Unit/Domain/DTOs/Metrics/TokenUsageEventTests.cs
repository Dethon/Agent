using Domain.DTOs.Metrics;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Metrics;

public class TokenUsageEventTests
{
    [Fact]
    public void TokenUsageEvent_OriginIsPersisted()
    {
        var evt = new TokenUsageEvent
        {
            Model = "tts-1",
            InputTokens = 0,
            OutputTokens = 0,
            Cost = 0m,
            Sender = "voice-sat",
            AgentId = null,
            Origin = "voice"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(evt,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        json.ShouldContain("\"origin\":\"voice\"");
    }
}