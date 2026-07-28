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
}