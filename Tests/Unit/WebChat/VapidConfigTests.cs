using Shouldly;
using global::WebChat.Client.Services;

namespace Tests.Unit.WebChat;

public sealed class VapidConfigTests
{
    [Fact]
    public void AppConfig_WithVapidPublicKey_IncludesIt()
    {
        var config = new AppConfig("http://localhost:5000", [], "BTestPublicKey123");

        config.VapidPublicKey.ShouldBe("BTestPublicKey123");
    }

    [Fact]
    public void AppConfig_WithoutVapidPublicKey_IsNull()
    {
        var config = new AppConfig("http://localhost:5000", [], null);

        config.VapidPublicKey.ShouldBeNull();
    }

    [Fact]
    public void AppConfig_BackwardCompatibility_TwoArgsStillWorks()
    {
        var config = new AppConfig(null, []);

        config.VapidPublicKey.ShouldBeNull();
        config.AgentUrl.ShouldBeNull();
        config.Users.ShouldBeEmpty();
    }

    [Fact]
    public void AppConfig_EmptyStringVapidKey_IsPreservedNotNull()
    {
        var config = new AppConfig("http://localhost:5000", [], "");

        config.VapidPublicKey.ShouldNotBeNull();
        config.VapidPublicKey.ShouldBeEmpty();
    }

    [Fact]
    public void AppConfig_VapidPublicKey_DoesNotExposePrivateKey()
    {
        var config = new AppConfig("http://localhost:5000", [], "BPublicKeyOnly");

        var properties = typeof(AppConfig).GetProperties();
        properties.ShouldNotContain(p => p.Name.Contains("Private", System.StringComparison.OrdinalIgnoreCase));
        properties.ShouldNotContain(p => p.Name.Contains("Secret", System.StringComparison.OrdinalIgnoreCase));
    }
}
