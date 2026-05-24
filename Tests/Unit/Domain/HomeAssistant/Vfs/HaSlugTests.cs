using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaSlugTests
{
    [Theory]
    [InlineData("Kitchen", "kitchen")]
    [InlineData("Aire Acondicionado Salón", "aire-acondicionado-salon")]
    [InlineData("Salón (1/2)", "salon-1-2")]
    [InlineData("  Multiple   Spaces  ", "multiple-spaces")]
    [InlineData("Año_Niño", "ano-nino")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("!!!", "")]
    public void Slugify_ProducesSafeSlug(string input, string expected) =>
        HaSlug.Slugify(input).ShouldBe(expected);

    [Fact]
    public void Slugify_CapsLengthAtWordBoundary()
    {
        var slug = HaSlug.Slugify(string.Join(' ', Enumerable.Repeat("word", 40)));
        slug.Length.ShouldBeLessThanOrEqualTo(60);
        slug.ShouldNotEndWith("-");
    }

    [Fact]
    public void Compose_AppendsNiceName()
    {
        HaSlug.Compose("climate.0x00158d00abcd", "Aire Acondicionado Salón")
            .ShouldBe("climate.0x00158d00abcd_(aire-acondicionado-salon)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void Compose_NoUsableName_ReturnsBareId(string? name) =>
        HaSlug.Compose("light.kitchen", name).ShouldBe("light.kitchen");

    [Theory]
    [InlineData("climate.0x00158d00abcd_(aire-acondicionado-salon)", "climate.0x00158d00abcd")]
    [InlineData("ac_salon_(aire-acondicionado-salon)", "ac_salon")]
    [InlineData("light.kitchen", "light.kitchen")]
    public void StripNice_RecoversId(string segment, string expected) =>
        HaSlug.StripNice(segment).ShouldBe(expected);

    [Fact]
    public void StripNice_AdversarialSuffix_StillRecoversId()
    {
        // The id has no '(', so the first "_(" always delimits; anything after is decorative.
        HaSlug.StripNice("climate.ac_(a)_(b)").ShouldBe("climate.ac");
    }
}