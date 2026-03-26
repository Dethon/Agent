using System.Reflection;
using System.Text.Json;
using Shouldly;
using WebChat.Client.Services;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class VapidConfigTests
{
    [Fact]
    public void AppConfig_Deserialize_WithoutVapidPublicKey_DefaultsToNull()
    {
        // Backward compatibility: server may not include VapidPublicKey yet
        const string json = """{"AgentUrl":"http://localhost:5000","Users":[]}""";

        var config = JsonSerializer.Deserialize<AppConfig>(json);

        config.ShouldNotBeNull();
        config.AgentUrl.ShouldBe("http://localhost:5000");
        config.VapidPublicKey.ShouldBeNull();
    }

    [Fact]
    public void AppConfig_Deserialize_WithVapidPublicKeyPresent_SetsValue()
    {
        const string json = """{"AgentUrl":"http://localhost:5000","Users":[],"VapidPublicKey":"BKey123"}""";

        var config = JsonSerializer.Deserialize<AppConfig>(json);

        config.ShouldNotBeNull();
        config.VapidPublicKey.ShouldBe("BKey123");
    }

    [Fact]
    public void AppConfig_NoPrivateKeyProperty_Exists()
    {
        // Security: AppConfig must never expose a private key property
        var properties = typeof(AppConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var propertyNames = properties.Select(p => p.Name).ToList();

        propertyNames.ShouldNotContain("PrivateKey");
        propertyNames.ShouldNotContain("VapidPrivateKey");
        propertyNames.ShouldNotContain("WebPushPrivateKey");

        // Also verify no property name contains "private" (case-insensitive)
        propertyNames.ShouldAllBe(
            name => !name.Contains("Private", StringComparison.OrdinalIgnoreCase),
            "AppConfig must not contain any property with 'Private' in its name");
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