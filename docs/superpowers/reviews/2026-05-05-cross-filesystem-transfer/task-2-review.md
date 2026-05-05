# Task 2: BlobReadTool base class

## Spec Compliance
- Reviewer: ✅ Spec compliant. `public class BlobReadTool(string rootPath)` with `public const int MaxChunkSizeBytes`, `protected const string Description`, `protected JsonNode Run(string path, long offset, int length)`. Returns `{contentBase64, eof, totalBytes}`. Path validation reuses CopyTool's separator-bounded sandbox check.

## Code Quality
- Reviewer: ✅ Approved (after one fix iteration).
- Initial round flagged two correctness issues:
  1. Negative `length` would surface as `OverflowException` from `new byte[toRead]` rather than a clean `ArgumentOutOfRangeException` at the boundary.
  2. Hand-rolled accumulation loop encoded the full pre-allocated buffer to base64; if `Stream.Read` returned short (file truncated mid-read), trailing zeros were silently encoded as content.
- Fix iteration: added `ArgumentOutOfRangeException.ThrowIfNegative` for offset and length; tracked actual bytes read and used `Convert.ToBase64String(buffer, 0, actuallyRead)` to encode only the populated portion. Added 3 new tests: empty file, offset-at-or-past-end, negative length.
- Final test count: 9 (5 spec + 1 sibling-prefix regression + 3 correctness coverage).
- Base SHA: 050d82f8abdd0118e6a553583a541e5459b29525
- Head SHA: 1ab85a238364e5129ca1ca00c14619e3c23c241e

## Resolution
- Issues found: 2 important + 3 missing tests
- Issues fixed: 2 important + 3 tests added
- Final status: ✅ Approved
