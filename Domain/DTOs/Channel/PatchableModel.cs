using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record PatchableModel(string Id, string Name);