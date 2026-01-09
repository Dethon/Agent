using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.RealEstate;

public class PropertySearchTool(IIdealistaClient idealistaClient)
{
    protected const string Name = "IdealistaPropertySearch";

    protected const string Description =
        """
        Searches for real estate properties on Idealista (Spain, Italy, Portugal).
        Supports filtering by property type (homes, offices, premises, garages, bedrooms),
        operation (sale, rent), location, price range, size, and many property-specific features.
        Returns property listings with details including price, location, size, rooms, and URLs.
        """;

    protected async Task<JsonNode> RunAsync(
        string country,
        string operation,
        string propertyType,
        string? locationId,
        string? center,
        double? distance,
        int? maxItems,
        int? numPage,
        double? minPrice,
        double? maxPrice,
        double? minSize,
        double? maxSize,
        string? bedrooms,
        string? bathrooms,
        string? order,
        string? sort,
        bool? elevator,
        bool? garage,
        bool? terrace,
        bool? swimmingPool,
        bool? airConditioning,
        bool? newDevelopment,
        string? preservation,
        CancellationToken ct)
    {
        var query = new IdealistaSearchQuery
        {
            Country = country,
            Operation = operation,
            PropertyType = propertyType,
            LocationId = locationId,
            Center = center,
            Distance = distance,
            MaxItems = maxItems ?? 20,
            NumPage = numPage ?? 1,
            MinPrice = minPrice,
            MaxPrice = maxPrice,
            MinSize = minSize,
            MaxSize = maxSize,
            Bedrooms = bedrooms,
            Bathrooms = bathrooms,
            Order = order,
            Sort = sort,
            Elevator = elevator,
            Garage = garage,
            Terrace = terrace,
            SwimmingPool = swimmingPool,
            AirConditioning = airConditioning,
            NewDevelopment = newDevelopment,
            Preservation = preservation
        };

        var result = await idealistaClient.SearchAsync(query, ct);

        if (result.ElementList.Count == 0)
        {
            return new JsonObject
            {
                ["status"] = "no_results",
                ["total"] = 0,
                ["properties"] = new JsonArray(),
                ["suggestion"] = "No properties found. Try adjusting your search filters or location."
            };
        }

        var propertiesArray = new JsonArray();
        foreach (var property in result.ElementList)
        {
            var propertyNode = new JsonObject
            {
                ["propertyCode"] = property.PropertyCode,
                ["operation"] = property.Operation,
                ["price"] = property.Price,
                ["url"] = property.Url
            };

            if (property.Address != null)
            {
                propertyNode["address"] = property.Address;
            }

            if (property.Municipality != null)
            {
                propertyNode["municipality"] = property.Municipality;
            }

            if (property.Province != null)
            {
                propertyNode["province"] = property.Province;
            }

            if (property.District != null)
            {
                propertyNode["district"] = property.District;
            }

            if (property.Size.HasValue)
            {
                propertyNode["size"] = property.Size.Value;
            }

            if (property.Rooms.HasValue)
            {
                propertyNode["rooms"] = property.Rooms.Value;
            }

            if (property.Bathrooms.HasValue)
            {
                propertyNode["bathrooms"] = property.Bathrooms.Value;
            }

            if (property.Floor != null)
            {
                propertyNode["floor"] = property.Floor;
            }

            if (property.Exterior.HasValue)
            {
                propertyNode["exterior"] = property.Exterior.Value;
            }

            if (property.HasLift.HasValue)
            {
                propertyNode["hasLift"] = property.HasLift.Value;
            }

            if (property.Status != null)
            {
                propertyNode["status"] = property.Status;
            }

            if (property.NewDevelopment.HasValue)
            {
                propertyNode["newDevelopment"] = property.NewDevelopment.Value;
            }

            if (property.PriceByArea.HasValue)
            {
                propertyNode["pricePerM2"] = Math.Round(property.PriceByArea.Value, 2);
            }

            if (property is { Latitude: not null, Longitude: not null })
            {
                propertyNode["coordinates"] = new JsonObject
                {
                    ["lat"] = property.Latitude.Value,
                    ["lng"] = property.Longitude.Value
                };
            }

            if (property.DetailedType != null)
            {
                var typeNode = new JsonObject();
                if (property.DetailedType.Typology != null)
                {
                    typeNode["typology"] = property.DetailedType.Typology;
                }

                if (property.DetailedType.Subtypology != null)
                {
                    typeNode["subtypology"] = property.DetailedType.Subtypology;
                }

                if (typeNode.Count > 0)
                {
                    propertyNode["detailedType"] = typeNode;
                }
            }

            if (property.ParkingSpace is { HasParkingSpace: true })
            {
                var parkingNode = new JsonObject
                {
                    ["hasParkingSpace"] = true
                };
                if (property.ParkingSpace.IsParkingSpaceIncludedInPrice.HasValue)
                {
                    parkingNode["includedInPrice"] = property.ParkingSpace.IsParkingSpaceIncludedInPrice.Value;
                }

                if (property.ParkingSpace.ParkingSpacePrice.HasValue)
                {
                    parkingNode["price"] = property.ParkingSpace.ParkingSpacePrice.Value;
                }

                propertyNode["parkingSpace"] = parkingNode;
            }

            propertiesArray.Add(propertyNode);
        }

        return new JsonObject
        {
            ["status"] = "success",
            ["total"] = result.Total,
            ["totalPages"] = result.TotalPages,
            ["currentPage"] = result.ActualPage,
            ["itemsPerPage"] = result.ItemsPerPage,
            ["summary"] = new JsonArray(result.Summary.Select(s => JsonValue.Create(s)).ToArray()),
            ["properties"] = propertiesArray
        };
    }
}