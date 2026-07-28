using System.Text;
using Domain.DTOs;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.Agent;

// Sort is an enum so a typo fails at bind time instead of shipping an unroutable value to
// OpenRouter. These pin that the binder actually behaves that way -- nothing else would catch
// it, because a bad sort would otherwise only surface as a silently ignored request field.
public class ProviderRoutingBindingTests
{
    [Theory]
    [InlineData("price", ProviderSort.Price)]
    [InlineData("throughput", ProviderSort.Throughput)]
    [InlineData("latency", ProviderSort.Latency)]
    [InlineData("Throughput", ProviderSort.Throughput)]
    public void Bind_ValidSort_MapsToMember(string configured, ProviderSort expected)
    {
        Bind(("providerRouting:sort", configured)).Sort.ShouldBe(expected);
    }

    [Fact]
    public void Bind_InvalidSort_ThrowsNamingThePath()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => Bind(("providerRouting:sort", "cheapest")));

        ex.Message.ShouldContain("providerRouting:sort");
    }

    // Enum.Parse accepts numeric strings including undefined values, so without a guard
    // "sort": 7 binds to (ProviderSort)7 and reaches the wire as "7" -- the exact silent
    // misconfiguration the enum exists to prevent. Alphabetic typos alone failing loudly
    // is not enough.
    // The binder reaches the property through reflection, so the guard surfaces wrapped
    // (today in TargetInvocationException); the wrapper type is implementation detail, the
    // ArgumentOutOfRangeException at the root of the chain is the contract.
    [Fact]
    public void Bind_UndefinedNumericSort_Throws()
    {
        var ex = Should.Throw<Exception>(() => Bind(("providerRouting:sort", "7")));

        ex.GetBaseException().ShouldBeOfType<ArgumentOutOfRangeException>()
            .Message.ShouldContain(nameof(ProviderSort));
    }

    [Fact]
    public void Construct_UndefinedSortValue_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new ProviderRouting { Sort = (ProviderSort)7 });
    }

    [Fact]
    public void Bind_ArraysAndFlags_MapFromIndexedKeys()
    {
        var routing = Bind(
            ("providerRouting:order:0", "deepinfra"),
            ("providerRouting:order:1", "novita"),
            ("providerRouting:only:0", "deepinfra"),
            ("providerRouting:ignore:0", "chutes"),
            ("providerRouting:allowFallbacks", "false"));

        routing.Order.ShouldBe(["deepinfra", "novita"]);
        routing.Only.ShouldBe(["deepinfra"]);
        routing.Ignore.ShouldBe(["chutes"]);
        routing.AllowFallbacks.ShouldBe(false);
    }

    [Fact]
    public void Bind_MissingSection_YieldsNull()
    {
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build()
            .GetSection("providerRouting")
            .Get<ProviderRouting>()
            .ShouldBeNull();
    }

    // The JSON provider records an empty object as a null-valued key, so `"providerRouting": {}`
    // binds to null and the agent INHERITS the global default -- {} is not a wholesale opt-out.
    // CLAUDE.md documents that trap; this pins the binder behavior the documentation relies on.
    // The key-presence assert keeps the null from ever meaning "the test's JSON lost the key".
    [Fact]
    public void Bind_EmptyJsonObject_YieldsNull()
    {
        var config = BuildJson("""{"providerRouting": {}}""");

        config.GetChildren().Select(c => c.Key).ShouldContain("providerRouting");
        config.GetSection("providerRouting").Get<ProviderRouting>().ShouldBeNull();
    }

    // The working opt-out spelling for a future non-empty global default:
    // {"allowFallbacks": true} binds to a real instance -- so it shadows the global wholesale --
    // whose only wire effect is `allow_fallbacks: true`, OpenRouter's default, leaving the
    // agent on balanced routing.
    [Fact]
    public void Bind_AllowFallbacksOnly_YieldsAnInstanceThatShadowsButStaysBalanced()
    {
        var routing = BuildJson("""{"providerRouting": {"allowFallbacks": true}}""")
            .GetSection("providerRouting")
            .Get<ProviderRouting>();

        routing.ShouldNotBeNull();
        routing.IsEmpty.ShouldBeFalse();
        routing.Sort.ShouldBeNull();
        routing.Order.ShouldBeNull();
    }

    [Fact]
    public void IsEmpty_NoFieldsSet_IsTrue()
    {
        new ProviderRouting().IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void IsEmpty_EmptyArrays_IsTrue()
    {
        new ProviderRouting { Order = [], Only = [], Ignore = [] }.IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(NonEmptyRoutings))]
    public void IsEmpty_AnyFieldSet_IsFalse(string _, ProviderRouting routing)
    {
        routing.IsEmpty.ShouldBeFalse();
    }

    public static IEnumerable<object[]> NonEmptyRoutings =>
    [
        ["sort", new ProviderRouting { Sort = ProviderSort.Price }],
        ["order", new ProviderRouting { Order = ["deepinfra"] }],
        ["only", new ProviderRouting { Only = ["deepinfra"] }],
        ["ignore", new ProviderRouting { Ignore = ["chutes"] }],
        ["allowFallbacks", new ProviderRouting { AllowFallbacks = false }]
    ];

    private static ProviderRouting Bind(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value))
            .Build()
            .GetSection("providerRouting")
            .Get<ProviderRouting>()!;

    private static IConfigurationRoot BuildJson(string json) =>
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
}