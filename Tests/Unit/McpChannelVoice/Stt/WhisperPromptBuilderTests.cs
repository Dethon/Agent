using McpChannelVoice.Services.Stt;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class WhisperPromptBuilderTests
{
    [Fact]
    public void Build_TemplateOnly_SubstitutesRoomAndLocality()
    {
        var prompt = WhisperPromptBuilder.Build(
            "Órdenes en {room}, {locality}.", "la cocina", "Valladolid", null, 700);

        prompt.ShouldBe("Órdenes en la cocina, Valladolid.");
    }

    [Fact]
    public void Build_MissingLocality_CollapsesTheGapItLeaves()
    {
        var prompt = WhisperPromptBuilder.Build(
            "Órdenes en {room} {locality} ahora.", "la cocina", null, null, 700);

        prompt.ShouldBe("Órdenes en la cocina ahora.");
    }

    [Fact]
    public void Build_UnknownPlaceholder_IsLeftLiteral()
    {
        var prompt = WhisperPromptBuilder.Build("Pon {algo} en {room}.", "el salón", null, null, 700);

        prompt.ShouldBe("Pon {algo} en el salón.");
    }

    [Fact]
    public void Build_PriorText_GoesLastSoItSitsClosestToTheAudio()
    {
        var prompt = WhisperPromptBuilder.Build("Órdenes breves.", null, null, "pon el temporizador", 700);

        prompt.ShouldBe("Órdenes breves. pon el temporizador");
    }

    [Fact]
    public void Build_OverBudget_TrimsPriorTextFromItsFrontAtAWordBoundary()
    {
        // Static is 6 chars; a 20-char cap leaves 13 for the prior text after the joining space.
        var prompt = WhisperPromptBuilder.Build("Manda.", null, null, "uno dos tres cuatro", 20);

        prompt.ShouldBe("Manda. tres cuatro");
    }

    [Fact]
    public void Build_StaticAloneOverBudget_KeepsItWholeAndDropsPriorText()
    {
        var prompt = WhisperPromptBuilder.Build("Un texto largo de verdad.", null, null, "hola", 10);

        prompt.ShouldBe("Un texto largo de verdad.");
    }

    [Fact]
    public void Build_PriorTextOnly_IsTrimmedToTheBudget()
    {
        var prompt = WhisperPromptBuilder.Build(null, null, null, "uno dos tres cuatro", 12);

        prompt.ShouldBe("tres cuatro");
    }

    [Fact]
    public void Build_PriorTextWordLongerThanTheBudget_DropsItInsteadOfCuttingMidWord()
    {
        // Static is 6 chars; a 15-char cap leaves 8 for the prior text, and its only word is 19.
        var prompt = WhisperPromptBuilder.Build("Manda.", null, null, "extraordinariamente", 15);

        prompt.ShouldBe("Manda.");
    }

    [Fact]
    public void Build_PriorTextOnlyWordLongerThanTheBudget_ReturnsNull()
    {
        WhisperPromptBuilder.Build(null, null, null, "extraordinariamente", 8).ShouldBeNull();
    }

    [Fact]
    public void Build_BudgetCutOnAWordBoundary_KeepsTheWholeWord()
    {
        WhisperPromptBuilder.Build(null, null, null, "uno dos", 3).ShouldBe("dos");
    }

    [Fact]
    public void Build_NothingToSay_ReturnsNull()
    {
        WhisperPromptBuilder.Build(null, "la cocina", "Valladolid", null, 700).ShouldBeNull();
        WhisperPromptBuilder.Build("   ", null, null, "  ", 700).ShouldBeNull();
    }

    [Fact]
    public void Build_TemplateThatResolvesToNothing_ReturnsNull()
    {
        WhisperPromptBuilder.Build("{room}", null, null, null, 700).ShouldBeNull();
    }

    [Fact]
    public void OverBudgetPromptSources_GlobalPromptOverTheCap_NamesIt()
    {
        var settings = new VoiceSettings
        {
            Stt = new SttSettings
            {
                OpenAi = new OpenAiSttConfig { Prompt = new string('a', 20), MaxPromptChars = 10 }
            }
        };

        WhisperPromptBuilder.OverBudgetPromptSources(settings).ShouldBe(["Stt:OpenAi:Prompt"]);
    }

    [Fact]
    public void OverBudgetPromptSources_SatelliteOverrideOverTheCap_NamesItsPath()
    {
        var settings = new VoiceSettings
        {
            Stt = new SttSettings { OpenAi = new OpenAiSttConfig { MaxPromptChars = 10 } },
            Satellites = new Dictionary<string, SatelliteConfig>
            {
                ["kitchen-01"] = new()
                {
                    Identity = "household",
                    Room = "Kitchen",
                    Stt = new SttOverrides { OpenAi = new OpenAiSttOverrides { Prompt = new string('b', 20) } }
                }
            }
        };

        WhisperPromptBuilder.OverBudgetPromptSources(settings)
            .ShouldBe(["Satellites:kitchen-01:Stt:OpenAi:Prompt"]);
    }

    [Fact]
    public void OverBudgetPromptSources_EverythingWithinTheCap_IsEmpty()
    {
        var settings = new VoiceSettings
        {
            Stt = new SttSettings
            {
                OpenAi = new OpenAiSttConfig { Prompt = "corto", MaxPromptChars = 700 }
            }
        };

        WhisperPromptBuilder.OverBudgetPromptSources(settings).ShouldBeEmpty();
    }
}