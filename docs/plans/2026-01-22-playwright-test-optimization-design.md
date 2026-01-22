# Playwright Integration Test Optimization

## Problem

Playwright integration tests take 30+ seconds per test, which is excessive even accounting for network latency.

## Root Causes

1. **Default WaitStrategy is NetworkIdle** - Waits for ALL network activity to stop, which can take 10-30s on modern sites with analytics/trackers
2. **Modal dismissal overhead** - Tries 4 modal types × ~8 selectors × 500-3000ms timeouts each, even on sites without modals
3. **Excessive timeouts** - Tests use 10000-15000ms timeouts when 5000ms would suffice for simple sites

## Solution

Optimize test parameters without changing production code:

1. Switch wait strategy from default (NetworkIdle) to explicit `DomContentLoaded` for most tests
2. Disable modal dismissal (`DismissModals: false`) for sites that don't need it
3. Reduce timeouts to appropriate values per site

## Test Changes

| Test | Changes |
|------|---------|
| Simple sites (example.com, httpbin.org) | DomContentLoaded, DismissModals: false, 5000ms |
| Wikipedia tests | DomContentLoaded, DismissModals: false, 8000ms |
| DuckDuckGo tests | Keep NetworkIdle (SPA needs it), reduce to 10000ms |
| Error/edge case tests | DomContentLoaded, 5000ms |

## Expected Impact

Tests should drop from 30+ seconds to 3-8 seconds each.
