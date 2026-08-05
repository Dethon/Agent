using Domain.Tools;

namespace Domain.Contracts;

// A backend that refuses to move some of its paths, for a cross-mount move that never asks it.
//
// A same-mount move goes through the backend's own MoveAsync, so a refusal lives there. A
// cross-mount move does not: it streams the bytes to the other backend and then deletes the
// source, so the refusal is bypassed and the delete runs as the tail of a move the same-mount
// path rejects. VfsMoveTool asks both ends of a cross-mount move through this, before the first
// byte is transferred, so nothing is half-copied when the answer is no.
//
// This is a runtime refusal about one path, not a capability: the registrar advertises operations,
// and a backend may still turn down particular paths.
public interface ICrossMountMoveGuard
{
    Task<ToolErrorResult?> RefuseMoveAsync(string relativePath, CancellationToken ct);
}