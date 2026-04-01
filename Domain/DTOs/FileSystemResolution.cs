using Domain.Contracts;

namespace Domain.DTOs;

public record FileSystemResolution(IFileSystemBackend Backend, string RelativePath);
