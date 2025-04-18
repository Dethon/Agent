using Domain.DTOs;

namespace Domain.Contracts;

public interface ITool
{
    string Name { get; }
    ToolDefinition GetToolDefinition();
}