using System.Text.Json;
using Shouldly;
using WebChat.Client.Services;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class VapidConfigTests
{
    [Theory]
    [InlineData("""{"AgentUrl":"http://localhost:5000","Users":[]}""", null)]
    [InlineData("""{"AgentUrl":"http://localhost:5000","Users":[],"VapidPublicKey":"BKey123"}""", "BKey123")]
    public void AppConfig_Deserialize_PascalCase_VapidPublicKey(string json, string? expectedKey)
    {
        // Backward compatibility: server may not include VapidPublicKey yet
        var config = JsonSerializer.Deserialize<AppConfig>(json);

        config.ShouldNotBeNull();
        config.AgentUrl.ShouldBe("http://localhost:5000");
        config.VapidPublicKey.ShouldBe(expectedKey);
    }

    [Fact]
    public void AppConfig_Serialized_DoesNotContainPrivateKey()
    {
        // Security: even with reflection tricks, serialization must not leak private keys
        var config = new AppConfig("http://localhost:5000", [], "BPublicKey123");
        var json = JsonSerializer.Serialize(config);

        json.ShouldNotContain("PrivateKey");
        json.ShouldNotContain("private");
    }

    [Fact]
    public void AppConfig_Deserialize_CamelCaseJson_WorksCorrectly()
    {
        // ASP.NET Core minimal APIs serialize with camelCase by default;
        // HttpClient.GetFromJsonAsync uses JsonSerializerDefaults.Web (case-insensitive)
        const string json = """{"agentUrl":"http://localhost:5000","users":[],"vapidPublicKey":"BKey456"}""";

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var config = JsonSerializer.Deserialize<AppConfig>(json, options);

        config.ShouldNotBeNull();
        config.AgentUrl.ShouldBe("http://localhost:5000");
        config.VapidPublicKey.ShouldBe("BKey456");
    }
}