using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.RealEstate;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerIdealista.McpTools;

[McpServerToolType]
public class McpPropertySearchTool(IIdealistaClient idealistaClient)
    : PropertySearchTool(idealistaClient)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        [Description("Country code: 'es' (Spain), 'it' (Italy), or 'pt' (Portugal)")]
        string country,
        [Description("Operation type: 'sale' or 'rent'")]
        string operation,
        [Description("Property type: 'homes', 'offices', 'premises', 'garages', or 'bedrooms'")]
        string propertyType,
        [Description(
            "Idealista location ID (e.g., '0-EU-ES-28' for Madrid province). Either locationId OR center+distance is required.")]
        string? locationId = null,
        [Description(
            "Geographic coordinates as 'latitude,longitude' (e.g., '40.416775,-3.703790' for Madrid center). Use with distance parameter.")]
        string? center = null,
        [Description("Search radius in meters from center point (e.g., 5000 for 5km). Required when using center.")]
        double? distance = null,
        [Description("Maximum results per page (1-50, default: 20)")]
        int? maxItems = 20,
        [Description("Page number for pagination (starts at 1)")]
        int? numPage = 1,
        [Description("Minimum price in euros")]
        double? minPrice = null,
        [Description("Maximum price in euros")]
        double? maxPrice = null,
        [Description("Minimum size in square meters")]
        double? minSize = null,
        [Description("Maximum size in square meters")]
        double? maxSize = null,
        [Description("Number of bedrooms as comma-separated values (e.g., '2,3' for 2 or 3 bedrooms). Use '4' for 4+.")]
        string? bedrooms = null,
        [Description("Number of bathrooms as comma-separated values (e.g., '1,2'). Use '3' for 3+.")]
        string? bathrooms = null,
        [Description(
            "Sort field: 'price', 'size', 'rooms', 'publicationDate', 'modificationDate', 'pricedown', 'distance'")]
        string? order = null,
        [Description("Sort direction: 'asc' or 'desc'")]
        string? sort = null,
        [Description("Filter for properties with elevator")]
        bool? elevator = null,
        [Description("Filter for properties with garage")]
        bool? garage = null,
        [Description("Filter for properties with terrace")]
        bool? terrace = null,
        [Description("Filter for properties with swimming pool")]
        bool? swimmingPool = null,
        [Description("Filter for properties with air conditioning")]
        bool? airConditioning = null,
        [Description("Filter for new development properties only")]
        bool? newDevelopment = null,
        [Description("Property condition: 'good' or 'renew'")]
        string? preservation = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(locationId) && string.IsNullOrEmpty(center))
        {
            return ToolResponse.Create(new InvalidOperationException(
                "Either 'locationId' or 'center' with 'distance' must be provided."));
        }

        if (!string.IsNullOrEmpty(center) && !distance.HasValue)
        {
            return ToolResponse.Create(new InvalidOperationException(
                "When using 'center', 'distance' parameter is required."));
        }

        var result = await RunAsync(
            country,
            operation,
            propertyType,
            locationId,
            center,
            distance,
            maxItems,
            numPage,
            minPrice,
            maxPrice,
            minSize,
            maxSize,
            bedrooms,
            bathrooms,
            order,
            sort,
            elevator,
            garage,
            terrace,
            swimmingPool,
            airConditioning,
            newDevelopment,
            preservation,
            ct);

        return ToolResponse.Create(result);
    }
}
