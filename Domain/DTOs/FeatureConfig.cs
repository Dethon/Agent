using Domain.Contracts;

namespace Domain.DTOs;

public record FeatureConfig(
    IToolApprovalHandler ApprovalHandler,
    string[] WhitelistPatterns,
    string UserId);
