# Task 17: Cross-FS binary roundtrip integration test

## Spec Compliance
- Reviewer: ✅ Spec compliant. New `[Fact]` `RunAsync_CrossFsBinaryFile_RoundtripsAllBytes` appended. 614400-byte sequence (`(i % 256)` for `i = 0..600*1024`) verifies all 256 byte values across 3 chunks (256+256+88 KiB). Asserts `status="ok"` + full byte equality. Reuses `Connect`/`BuildRegistry` helpers.

## Code Quality
- Reviewer: ✅ Approved. Pattern aligned. Byte-array assertion via `ShouldBe(bytes)` is exhaustive. McpClients disposed via `await using`. No duplication.
- GREEN: 1/1 passing.
- Base SHA: 94ec866fc9b50ac88009187a5308b45f366b29ae
- Head SHA: 6dc8893b7126a6d5d82554ff9fb9274fc892fb20

## Resolution
- Issues found: 1 minor (could route through a fixture helper like `fx.CreateLibraryFile(byte[])` instead of direct `File.WriteAllBytes` — fixture doesn't currently expose a binary variant)
- Issues fixed: 0 (deferred — not in scope)
- Final status: ✅ Approved
