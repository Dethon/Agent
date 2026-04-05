# Task 3: Extractor prompt + option fields

## Spec Compliance
- Reviewer (controller direct verification — pure config, TDD-exempt): ✅ Spec compliant. Diff shows exactly 3 edits in 3 files, all matching plan verbatim: `ExtractionSystemPrompt` rewritten for windowed input, `WindowUserTurns = 3` added to `MemoryRecallOptions`, `WindowMixedTurns = 6` added to `MemoryExtractionOptions`. No other changes.

## Code Quality
- Reviewer (controller direct verification): Approved. Record init-only properties follow project style. Raw-string prompt preserved. 1088/1088 unit tests still pass — behavior unchanged.
- Base SHA: 686d1ced
- Head SHA: aba2385c

## Resolution
- Issues found: 0
- Issues fixed: 0
- Final status: ✅ Approved
