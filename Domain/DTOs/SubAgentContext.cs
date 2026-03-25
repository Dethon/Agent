using Domain.Contracts;

namespace Domain.DTOs;

public record SubAgentContext(
    IToolApprovalHandler ApprovalHandler,
    string[] WhitelistPatterns,
    string UserId);
