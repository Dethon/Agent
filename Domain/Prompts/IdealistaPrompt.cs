namespace Domain.Prompts;

public static class IdealistaPrompt
{
    public const string Name = "idealista_guide";

    public const string Description =
        "Guide for using Idealista property search for real estate market research";

    public const string SystemPrompt =
        """
        ## Idealista Property Search

        Search real estate listings across Spain, Italy, and Portugal via the Idealista API.

        ### Market Research Strategies

        **Price Analysis**
        - Search by location with different price ranges to understand market distribution
        - Compare `priceByArea` (price per m²) across neighborhoods
        - Sort by `pricedown` to find recent price reductions

        **Location Comparison**
        - Use `center` + `distance` for radius searches around specific coordinates
        - Use `locationId` for administrative boundaries (provinces, cities)
        - Compare similar property types across different areas

        **Supply Analysis**
        - Check `total` in results to gauge inventory levels
        - Filter by `newDevelopment` to separate new construction from resale
        - Use `preservation` filter ('good' vs 'renew') to analyze property conditions

        **Trend Detection**
        - Sort by `publicationDate` desc to see newest listings
        - Sort by `modificationDate` to find recently updated prices
        - Track `priceByArea` over multiple searches to spot trends

        ### Tips

        - Start broad, then narrow with filters
        - Use pagination (`numPage`) to get complete market picture
        - Combine filters strategically: bedrooms + size + price narrows to specific segments
        - Property URLs in results link directly to full listings
        """;
}