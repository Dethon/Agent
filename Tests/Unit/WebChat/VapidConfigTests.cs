using Shouldly;
using WebChat.Client.Services;

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
}
