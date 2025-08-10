﻿namespace Domain.DTOs;

public record AiResponse
{
    public string Content { get; init; } = string.Empty;
    public string ToolCalls { get; init; } = string.Empty;
}