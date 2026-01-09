namespace Domain.Contracts;

public interface IIdealistaClient
{
    Task<IdealistaSearchResult> SearchAsync(IdealistaSearchQuery query, CancellationToken ct = default);
}

public record IdealistaSearchQuery
{
    public required string Country { get; init; }
    public required string Operation { get; init; }
    public required string PropertyType { get; init; }
    public string? Center { get; init; }
    public double? Distance { get; init; }
    public string? LocationId { get; init; }
    public string? Locale { get; init; }
    public int? MaxItems { get; init; }
    public int? NumPage { get; init; }
    public double? MaxPrice { get; init; }
    public double? MinPrice { get; init; }
    public string? SinceDate { get; init; }
    public string? Order { get; init; }
    public string? Sort { get; init; }
    public bool? HasMultimedia { get; init; }

    // Size filters (homes, offices, premises)
    public double? MinSize { get; init; }
    public double? MaxSize { get; init; }

    // Home specific
    public bool? Flat { get; init; }
    public bool? Penthouse { get; init; }
    public bool? Duplex { get; init; }
    public bool? Studio { get; init; }
    public bool? Chalet { get; init; }
    public bool? CountryHouse { get; init; }
    public string? Bedrooms { get; init; }
    public string? Bathrooms { get; init; }
    public string? Preservation { get; init; }
    public bool? NewDevelopment { get; init; }
    public string? Furnished { get; init; }
    public bool? Garage { get; init; }
    public bool? Terrace { get; init; }
    public bool? Exterior { get; init; }
    public bool? Elevator { get; init; }
    public bool? SwimmingPool { get; init; }
    public bool? AirConditioning { get; init; }
    public bool? StoreRoom { get; init; }
    public bool? BuiltinWardrobes { get; init; }
    public string? SubTypology { get; init; }

    // Garage specific
    public bool? AutomaticDoor { get; init; }
    public bool? MotorcycleParking { get; init; }
    public bool? Security { get; init; }

    // Premise specific
    public string? Location { get; init; }
    public bool? Corner { get; init; }
    public bool? SmokeVentilation { get; init; }
    public bool? Heating { get; init; }
    public bool? Transfer { get; init; }
    public string? BuildingTypes { get; init; }

    // Office specific
    public string? Layout { get; init; }
    public string? BuildingType { get; init; }
    public bool? HotWater { get; init; }

    // Room specific
    public string? Housemates { get; init; }
    public string? SmokePolicy { get; init; }
    public bool? PetsPolicy { get; init; }
    public bool? GayPartners { get; init; }
    public string? NewGender { get; init; }

    // Bank offer (sale in Spain)
    public bool? BankOffer { get; init; }
    public bool? VirtualTour { get; init; }
}

public record IdealistaSearchResult
{
    public required int ActualPage { get; init; }
    public required int ItemsPerPage { get; init; }
    public required int Total { get; init; }
    public required int TotalPages { get; init; }
    public required bool Paginable { get; init; }
    public IReadOnlyList<string> Summary { get; init; } = [];
    public required IReadOnlyList<IdealistaProperty> ElementList { get; init; }
}

public record IdealistaProperty
{
    public required string PropertyCode { get; init; }
    public string? Address { get; init; }
    public string? District { get; init; }
    public string? Municipality { get; init; }
    public string? Province { get; init; }
    public string? Neighborhood { get; init; }
    public string? Region { get; init; }
    public string? Country { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? Distance { get; init; }
    public string? Url { get; init; }
    public string? Thumbnail { get; init; }
    public int? NumPhotos { get; init; }
    public bool? HasVideo { get; init; }
    public required string Operation { get; init; }
    public required int Price { get; init; }
    public double? PriceByArea { get; init; }
    public string? PropertyType { get; init; }
    public int? Size { get; init; }
    public int? Rooms { get; init; }
    public int? Bathrooms { get; init; }
    public string? Floor { get; init; }
    public bool? Exterior { get; init; }
    public bool? HasLift { get; init; }
    public bool? ShowAddress { get; init; }
    public string? Status { get; init; }
    public bool? NewDevelopment { get; init; }
    public IdealistaParkingSpace? ParkingSpace { get; init; }
    public IdealistaDetailedType? DetailedType { get; init; }
    public string? ExternalReference { get; init; }
    public string? GarageType { get; init; }
    public string? TenantGender { get; init; }
    public bool? IsSmokingAllowed { get; init; }
}

public record IdealistaParkingSpace
{
    public bool? HasParkingSpace { get; init; }
    public bool? IsParkingSpaceIncludedInPrice { get; init; }
    public double? ParkingSpacePrice { get; init; }
}

public record IdealistaDetailedType
{
    public string? Typology { get; init; }
    public string? Subtypology { get; init; }
}