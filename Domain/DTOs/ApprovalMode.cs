using System.Text.Json.Serialization;
using Domain.Json;

namespace Domain.DTOs;

[JsonConverter(typeof(SnakeCaseLowerEnumConverter<ApprovalMode>))]
public enum ApprovalMode
{
    Request,
    Notify
}
