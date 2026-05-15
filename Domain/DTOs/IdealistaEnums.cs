using System.Text.Json.Serialization;
using Domain.Json;

namespace Domain.DTOs;

[JsonConverter(typeof(SnakeCaseLowerEnumConverter<IdealistaCountry>))]
public enum IdealistaCountry
{
    Es,
    It,
    Pt
}

[JsonConverter(typeof(SnakeCaseLowerEnumConverter<IdealistaOperation>))]
public enum IdealistaOperation
{
    Sale,
    Rent
}

[JsonConverter(typeof(SnakeCaseLowerEnumConverter<IdealistaPropertyType>))]
public enum IdealistaPropertyType
{
    Homes,
    Offices,
    Premises,
    Garages,
    Bedrooms
}

[JsonConverter(typeof(CamelCaseEnumConverter<IdealistaSortField>))]
public enum IdealistaSortField
{
    Price,
    Size,
    Rooms,
    PublicationDate,
    ModificationDate,
    Pricedown,
    Distance
}

[JsonConverter(typeof(SnakeCaseLowerEnumConverter<IdealistaSortDirection>))]
public enum IdealistaSortDirection
{
    Asc,
    Desc
}

[JsonConverter(typeof(SnakeCaseLowerEnumConverter<IdealistaPreservation>))]
public enum IdealistaPreservation
{
    Good,
    Renew
}