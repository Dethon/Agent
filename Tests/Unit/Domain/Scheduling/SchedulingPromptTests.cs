using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Scheduling;

public class SchedulingPromptTests
{
    [Fact]
    public void Prompt_EmbedsValidRecurringAndOneShotJsonExamples()
    {
        var roots = Regex.Matches(SchedulingPrompt.Prompt, "```json\\s*(\\{.*?\\})\\s*```", RegexOptions.Singleline)
            .Select(m =>
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                return doc.RootElement.Clone();
            })
            .ToList();

        roots.ShouldNotBeEmpty();
        roots.ShouldAllBe(e => Has(e, "prompt"));
        roots.ShouldContain(e => Has(e, "cron"));
        roots.ShouldContain(e => Has(e, "runAt"));
    }

    private static bool Has(JsonElement element, string property) => element.TryGetProperty(property, out _);
}