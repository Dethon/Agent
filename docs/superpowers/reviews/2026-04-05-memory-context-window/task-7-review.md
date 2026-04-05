# Task 7: Integration test — async drift

## Spec Compliance
- Reviewer (controller): ✅ Spec compliant. Exactly 1 new file created (`Tests/Integration/Memory/MemoryExtractionWorkerDriftTests.cs`, 87 lines). Test body matches plan verbatim. Real `RedisStackMemoryStore` + `RedisThreadStateStore` used, no mocks for the thread store. Test seeds 3 messages, enqueues anchor=2, appends 3 more messages (including a drift turn "Actually, make it Thailand"), then runs the worker — asserts the captured window does not contain Thailand/"Great choice!" and ends at "Japan in April".

## Code Quality
- Reviewer (controller): Approved. Single `[Trait("Category", "Integration")]` test, correct fixture pattern (`IClassFixture<RedisFixture>`), primary-constructor DI, no unnecessary abstractions. No scope creep.
- Base SHA: 464adaba
- Head SHA: c90d4162

## Resolution
- Issues found: 0
- Issues fixed: 0
- Final status: ✅ Approved. Integration test passes against real Redis (115 ms). 22/22 memory tests pass.
