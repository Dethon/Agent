using System.ComponentModel;

namespace Domain.DTOs;

public record TextEdit(
    [property: Description("Exact text to find (case-sensitive)")]
    string OldString,
    [property: Description("Replacement text")]
    string NewString,
    [property: Description("Replace all occurrences (default: false)")]
    bool ReplaceAll = false);
