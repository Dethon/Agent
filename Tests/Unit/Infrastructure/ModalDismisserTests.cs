using Domain.Contracts;
using Infrastructure.Clients.Browser;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class ModalDismisserTests
{
    [Fact]
    public void TextPatternRegexFor_CustomPatternReusingADefaultType_MatchesItsOwnTextsNotTheDefaults()
    {
        // The cache used to be keyed by ModalType, so a pattern constructed with a default Type but
        // its own text list silently got the DEFAULT union regex: Playwright then selected only the
        // default names, the pattern's own texts matched nothing, and the pattern was dead with no
        // hint as to why.
        var custom = new ModalPattern(
            ModalType.CookieConsent, ContainerSelector: null, ButtonSelectors: [],
            ButtonTextPatterns: ["botón raro"]);

        var regex = ModalDismisser.TextPatternRegexFor(custom);

        regex.IsMatch("Botón raro").ShouldBeTrue();
        regex.IsMatch("accept").ShouldBeFalse();
    }
}