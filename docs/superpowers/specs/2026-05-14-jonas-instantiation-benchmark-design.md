# Jonas Instantiation Benchmark — Design

## Goal

Add a small benchmark that measures how long it takes to instantiate an `McpAgent` configured as the `jonas` agent definition, including the first session creation (which is where MCP server connections happen).

This is a measurement, not a correctness or perf-gate test. The output is wall-clock timing reported via test output for humans to read.

## Scope

The benchmark times the following per iteration:

```csharp
sw.Restart();
var agent = factory.Create(agentKey, userId, "jonas", approvalHandler);
await using var session = await agent.CreateSessionAsync(ct);
sw.Stop();
```

This boundary covers:

- Resolving the `jonas` `AgentDefinition` via `IAgentDefinitionProvider`.
- Constructing `OpenRouterChatClient` and wrapping it in `ToolApprovalChatClient`.
- Building the domain tool/prompt set for jonas's `EnabledFeatures` (`filesystem`, `scheduling`, `subagents`, `memory`).
- Constructing the `McpAgent` instance.
- First `CreateSessionAsync` — the first network-bound step, which connects over HTTP to each of jonas's 5 MCP endpoints: `mcp-vault`, `mcp-sandbox`, `mcp-websearch`, `mcp-idealista`, `mcp-homeassistant`.

Out of scope:

- Running the agent. No `RunStreamingAsync` call is timed.
- DI bootstrap time. The service provider is built once outside the timing loop.
- Long-running session reuse. Each iteration disposes the agent.

## Location and shape

- New file: `Tests/Integration/Benchmarks/JonasInstantiationBenchmark.cs`.
- Single test method, `[SkippableFact]`, decorated with `[Trait("Category", "Benchmark")]` so it can be filtered out of normal CI runs.
- Uses the existing `RedisFixture` for `IConnectionMultiplexer` / `IThreadStateStore`.

## DI wiring

The benchmark builds its own `ServiceCollection` rather than booting the full Agent host. Approach:

1. Load `Agent/appsettings.json` via `ConfigurationBuilder` so the canonical `jonas` definition is what gets timed (no duplicated config to drift).
2. Add user secrets so the OpenRouter API key is available (same convention as existing integration tests).
3. Call `services.AddAgent(settings)` from `Agent.Modules.InjectorModule` to get the production wiring for `IAgentFactory`, `IDomainToolRegistry`, `IAgentDefinitionProvider`, and Redis services.
4. Add the feature modules jonas requires (`filesystem`, `scheduling`, `subagents`, `memory`) by reusing the same module extension methods that the Agent project's `Program.cs` calls. Hosted services and channel monitoring are not registered.
5. If a module registers a hosted service or background worker that would start on resolution, register a narrower equivalent or skip that module — the benchmark is for instantiation cost, not for end-to-end agent operation.
6. Provide a throwaway `IToolApprovalHandler` (auto-reject). No tool calls are made during the timed path, so its behavior is irrelevant.
7. No `IMetricsPublisher` — pass `null`.

If during implementation it turns out a feature module needs services that aren't trivial to stand up (e.g., the memory module's Redis Stack vector index), the implementation will pause and confirm with the user before going further. We do not silently expand the benchmark's setup beyond what's needed.

## Skip conditions

The test is a `[SkippableFact]`. It skips (does not fail) when:

- `openRouter:apiKey` is missing from user secrets.
- Any of jonas's 5 MCP endpoints fails a quick TCP probe (short timeout, e.g. 500 ms each).

This matches the pattern used elsewhere in `Tests/Integration/Agents`.

## Iteration plan

- 1 warmup iteration — discarded. Absorbs JIT cost, `HttpClient` pool initialization, TLS handshake cost, and Redis warm-up.
- 5 measured iterations.
- Each iteration:
  1. `Stopwatch.Restart()`.
  2. `factory.Create(agentKey, userId, "jonas", approvalHandler)`.
  3. `await agent.CreateSessionAsync(cts.Token)`.
  4. `Stopwatch.Stop()`.
  5. `await session.DisposeAsync()` / `await agent.DisposeAsync()`.
- Total runtime cap per iteration: 2 minutes via `CancellationTokenSource`.

## Output

Per iteration, the test writes to `ITestOutputHelper`:

```
[iteration 1] 4321 ms
[iteration 2] 3987 ms
...
```

After the loop, a summary:

```
[summary] iterations=5 min=3712 ms mean=4044 ms max=4321 ms
```

No assertion on the timing values — purely informational. The only assertion is that no iteration's wall-clock time exceeded the CT cap, i.e. it didn't hang.

## Files touched

- New: `Tests/Integration/Benchmarks/JonasInstantiationBenchmark.cs`.
- Possibly new: a no-op `IToolApprovalHandler` implementation in the same file if one doesn't already exist in tests.
- No production code changes.

## Out of scope

- BenchmarkDotNet or any new perf framework. We're using `Stopwatch` and xUnit output.
- Multi-agent comparison. Only jonas is timed.
- Gating CI on the timing. The test is informational.
- Measuring cold-start of the full Agent host. The DI provider is built once, outside the loop.
