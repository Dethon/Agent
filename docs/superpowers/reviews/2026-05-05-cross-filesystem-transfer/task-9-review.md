# Task 9: Implement OpenReadStreamAsync (chunked materialising stream)

## Spec Compliance
- Reviewer: ✅ Spec compliant. `OpenReadStreamAsync` body matches spec verbatim — 256 KiB chunkSize, MemoryStream accumulator, `fs_blob_read` loop with `obj["ok"]` envelope check, base64 decode, EOF + zero-byte safety breaks, `Position = 0` rewind. Test asserts 600 KiB roundtrip via 3 chunks.

## Code Quality
- Reviewer: ✅ Approved. Loop correctness solid (eof + safety break), cancellation propagated, offset arithmetic correct (uses actual decoded bytes), error envelope detection verified against `BlobReadTool` (no `ok` key on success) and `ToolError` (`ok: false` on error). Caller-owned stream disposed via `await using`. Pattern aligned with sibling backend methods.
- Build clean. RED → GREEN evidence: NotImplementedException → 1/1 passing.
- Base SHA: b2e76f4e87be23849f83a01656cfd6e573fd637f
- Head SHA: d714cb680c0fa7fdd7dda4cdeab951096fa7f89b

## Resolution
- Issues found: 0 critical/important; 5 minor (edge-case test coverage, hard-failure-on-malformed-envelope, magic 256*1024 vs `BlobReadTool.MaxChunkSizeBytes`, optional safety comment)
- Issues fixed: 0 (deferred — all polish/optional, no functional impact)
- Final status: ✅ Approved
