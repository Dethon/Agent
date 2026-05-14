# Jonas Instantiation Benchmark Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an xUnit integration test that times how long it takes to instantiate the `jonas` agent (`MultiAgentFactory.Create("jonas") + agent.CreateSessionAsync()`) over a 1-warmup + 5-measured iteration loop, reporting min/mean/max via test output.

**Architecture:** Single test class in `Tests/Integration/Benchmarks/JonasInstantiationBenchmark.cs` that builds a production-equivalent DI graph (`AddAgent` + `AddScheduling` + `AddSubAgents` + `AddMemory` from the real `Agent.Modules` extensions), uses `RedisFixture` for state, loads the canonical `jonas` definition from `Agent/appsettings.json`, and times the factory call + first session creation with `Stopwatch`. `[SkippableFact]` skips when the OpenRouter API key or jonas's MCP endpoints aren't reachable.

**Tech Stack:** xUnit, `Xunit.SkippableFact`, `Shouldly`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Configuration`, `System.Diagnostics.Stopwatch`. No new NuGet dependencies.

**TDD note:** This is a measurement-only test — there is no production class under test, and no inverted assertion to write first. The project's Red-Green-Refactor rule applies to features and bug fixes; for an instrumentation test, that cycle does not produce useful artifacts. The plan instead builds the file incrementally with a verification step at the end of each task (compile, smoke-resolve, end-to-end run).

---

## File Structure

- Create: `Tests/Integration/Benchmarks/JonasInstantiationBenchmark.cs` — single self-contained file containing the test class, a file-scoped `AutoApproveHandler`, and any small helpers it needs. Mirrors the layout of `Tests/Integration/Agents/SubAgentTests.cs`.

No other files are created or modified.

---

### Task 1: Scaffold the benchmark file with skip logic

**Files:**
- Create: `Tests/Integration/Benchmarks/JonasInstantiationBenchmark.cs`

- [ ] **Step 1: Create the file with skeleton + skip logic**

```csharp
using System.Diagnostics;
using System.Net.Sockets;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Benchmarks;

[Trait("Category", "Benchmark")]
public class JonasInstantiationBenchmark(RedisFixture redisFixture)
    : IClassFixture<RedisFixture>
{
    private const string AgentId = "jonas";
    private const int WarmupIterations = 1;
    private const int MeasuredIterations = 5;
    private static readonly TimeSpan IterationTimeout = TimeSpan.FromMinutes(2);

    private static readonly IConfigurationRoot _configuration = new ConfigurationBuilder()
        .AddJsonFile(LocateAgentAppSettings(), optional: false)
        .AddUserSecrets<JonasInstantiationBenchmark>()
        .AddEnvironmentVariables()
        .Build();

    [SkippableFact]
    public async Task Create_Jonas_Benchmark()
    {
        Skip.If(string.IsNullOrEmpty(_configuration["openRouter:apiKey"]),
            "openRouter:apiKey not set in user secrets");

        var endpoints = _configuration
            .GetSection("agents")
            .GetChildren()
            .First(c => c["id"] == AgentId)
            .GetSection("mcpServerEndpoints")
            .Get<string[]>()
            ?? throw new InvalidOperationException($"agent '{AgentId}' has no mcpServerEndpoints");

        Skip.IfNot(await AllEndpointsReachable(endpoints, TimeSpan.FromMilliseconds(500)),
            $"One or more jonas MCP endpoints unreachable: {string.Join(", ", endpoints)}");

        await Task.CompletedTask;
    }

    private static string LocateAgentAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Agent", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate Agent/appsettings.json by walking up from test bin directory");
    }

    private static async Task<bool> AllEndpointsReachable(string[] endpoints, TimeSpan timeout)
    {
        var probes = endpoints.Select(ep => IsReachable(ep, timeout));
        var results = await Task.WhenAll(probes);
        return results.All(r => r);
    }

    private static async Task<bool> IsReachable(string endpoint, TimeSpan timeout)
    {
        try
        {
            var uri = new Uri(endpoint);
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync(uri.Host, uri.Port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

file sealed class AutoApproveHandler : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
        => Task.FromResult(ToolApprovalResult.Approved);

    public Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build Tests/Tests.csproj`
Expected: build succeeds.

- [ ] **Step 3: Verify the test skips cleanly when offline**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~JonasInstantiationBenchmark" --logger "console;verbosity=detailed"`
Expected: 1 test skipped (either "openRouter:apiKey not set in user secrets" or "One or more jonas MCP endpoints unreachable"). The test must not fail. The skip reason must match one of those two messages.

If the user secret is set AND all 5 endpoints are reachable, the test will currently *pass* with no measurement — that's expected; we add the loop in Task 3.

---

### Task 2: Wire up DI and resolve the agent factory

**Files:**
- Modify: `Tests/Integration/Benchmarks/JonasInstantiationBenchmark.cs`

- [ ] **Step 1: Add the DI helper method**

Add the following private method inside the `JonasInstantiationBenchmark` class, after `IsReachable`:

```csharp
private (ServiceProvider Provider, IAgentFactory Factory) BuildFactory()
{
    var settings = _configuration.Get<Agent.Settings.AgentSettings>()
        ?? throw new InvalidOperationException("Failed to bind AgentSettings from configuration");

    settings = settings with
    {
        Redis = settings.Redis with { ConnectionString = redisFixture.ConnectionString }
    };

    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(_configuration);
    services.AddLogging();

    services
        .AddAgent(settings)
        .AddScheduling()
        .AddSubAgents(settings.SubAgents)
        .AddMemory(_configuration);

    var provider = services.BuildServiceProvider();
    var factory = provider.GetRequiredService<IAgentFactory>();
    return (provider, factory);
}
```

Add the matching `using` directives at the top of the file:

```csharp
using Agent.Modules;
using Domain.Agents;
using Infrastructure.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
```

- [ ] **Step 2: Replace `Create_Jonas_Benchmark` body with a smoke-resolve check**

Replace the existing test body (everything inside the method after the skip lines) with:

```csharp
        var (provider, factory) = BuildFactory();
        try
        {
            factory.ShouldNotBeNull();
        }
        finally
        {
            await provider.DisposeAsync();
        }
```

- [ ] **Step 3: Verify the file compiles**

Run: `dotnet build Tests/Tests.csproj`
Expected: build succeeds. If `AgentSettings`, `RedisConfiguration`, or `SubAgents` aren't records with `with`-expression support, the build will fail — in that case, fall back to mutating the property directly (e.g. `settings.Redis.ConnectionString = redisFixture.ConnectionString;`) only if the type is mutable. If neither works, construct a new `AgentSettings` value explicitly using its constructor. Do NOT proceed past this step until the build is green.

- [ ] **Step 4: Verify the smoke test runs**

If the OpenRouter API key is in user secrets and the MCP endpoints are reachable, run:

```
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~JonasInstantiationBenchmark" --logger "console;verbosity=detailed"
```

Expected: 1 test passes (DI builds, factory resolves). If it fails with a missing-service error, identify which service and either:
- add the missing registration if it's something jonas genuinely needs, or
- escalate by stopping here and reporting the missing service — do not silently widen the DI graph.

If endpoints are unreachable or the secret is unset, expect a skip — same as Task 1.

---

### Task 3: Add the warmup + measured iteration loop

**Files:**
- Modify: `Tests/Integration/Benchmarks/JonasInstantiationBenchmark.cs`

- [ ] **Step 1: Add `ITestOutputHelper` to the constructor and field**

Change the class signature and add a field:

```csharp
public class JonasInstantiationBenchmark(RedisFixture redisFixture, ITestOutputHelper output)
    : IClassFixture<RedisFixture>
{
```

Add the `using` directive at the top:

```csharp
using Xunit.Abstractions;
```

(`Tests/Tests.csproj` pins `xunit` to `2.9.3`, where `ITestOutputHelper` lives in `Xunit.Abstractions`.)

- [ ] **Step 2: Replace the test body with the iteration loop**

Replace the smoke-resolve body added in Task 2 with the full loop:

```csharp
        var (provider, factory) = BuildFactory();
        try
        {
            var approvalHandler = new AutoApproveHandler();
            var userId = $"benchmark-user-{Guid.NewGuid()}";

            // Warmup — absorbs JIT, HttpClient pool init, TLS handshakes, Redis warm-up.
            for (var i = 0; i < WarmupIterations; i++)
            {
                await RunOneIterationAsync(factory, approvalHandler, userId);
            }

            var measured = new List<long>(MeasuredIterations);
            var stopwatch = new Stopwatch();
            for (var i = 0; i < MeasuredIterations; i++)
            {
                stopwatch.Restart();
                await RunOneIterationAsync(factory, approvalHandler, userId);
                stopwatch.Stop();
                measured.Add(stopwatch.ElapsedMilliseconds);
                output.WriteLine($"[iteration {i + 1}] {stopwatch.ElapsedMilliseconds} ms");
            }

            var min = measured.Min();
            var max = measured.Max();
            var mean = (long)measured.Average();
            output.WriteLine(
                $"[summary] iterations={MeasuredIterations} min={min} ms mean={mean} ms max={max} ms");

            measured.Count.ShouldBe(MeasuredIterations);
            measured.ShouldAllBe(t => t < IterationTimeout.TotalMilliseconds);
        }
        finally
        {
            await provider.DisposeAsync();
        }
```

- [ ] **Step 3: Add the per-iteration helper**

Add this private method to the class:

```csharp
private static async Task RunOneIterationAsync(
    IAgentFactory factory, IToolApprovalHandler approvalHandler, string userId)
{
    using var cts = new CancellationTokenSource(IterationTimeout);
    var agentKey = new AgentKey($"benchmark:{Guid.NewGuid()}");
    var agent = factory.Create(agentKey, userId, AgentId, approvalHandler);
    try
    {
        var session = await agent.CreateSessionAsync(cts.Token);
        await agent.DisposeThreadSessionAsync(session);
    }
    finally
    {
        await agent.DisposeAsync();
    }
}
```

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build Tests/Tests.csproj`
Expected: build succeeds.

If `AgentKey`'s constructor signature differs (e.g., takes two args), update the line `var agentKey = new AgentKey(...)` to match. The existing reference in `Tests/Integration/Agents/McpAgentTests.cs` (`new AgentKey("12345:67890")`) shows the single-string-arg form is valid.

If `DisposeThreadSessionAsync` is not the correct method to dispose a single session, check `Domain/Agents/DisposableAgent.cs` for the correct disposal method. The agent's own `DisposeAsync` in the `finally` block will dispose remaining sessions if needed.

- [ ] **Step 5: End-to-end verification (manual)**

Hand off to the user to run with the Docker Compose stack live:

```
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~JonasInstantiationBenchmark" --logger "console;verbosity=detailed"
```

Expected output includes:

```
[iteration 1] <NN> ms
[iteration 2] <NN> ms
[iteration 3] <NN> ms
[iteration 4] <NN> ms
[iteration 5] <NN> ms
[summary] iterations=5 min=<NN> ms mean=<NN> ms max=<NN> ms
```

The test must pass. Each iteration must complete well under 2 minutes (the `IterationTimeout` cap). The numbers are informational; there is no SLA.

If any iteration fails with an exception, do not retry blindly — capture the exception, identify the root cause (network, missing DI registration, MCP server error), and fix it before claiming success. Do not swallow exceptions to make the test pass.

---

### Task 4: Commit

- [ ] **Step 1: Stage and commit**

```bash
git add Tests/Integration/Benchmarks/JonasInstantiationBenchmark.cs
git commit -m "$(cat <<'EOF'
test: add jonas instantiation benchmark

Times MultiAgentFactory.Create("jonas") + first CreateSessionAsync over
1 warmup + 5 measured iterations, reporting min/mean/max via xUnit output.
SkippableFact when OpenRouter key or jonas MCP endpoints aren't reachable.
EOF
)"
```

- [ ] **Step 2: Verify commit**

Run: `git log --oneline -1`
Expected: shows the new commit with the expected subject line.

---

## Verification Summary

After Task 4, the deliverable is:
- One new file: `Tests/Integration/Benchmarks/JonasInstantiationBenchmark.cs`
- One commit on the current branch
- No production code changed
- No new NuGet dependencies
- Test passes when the Docker Compose stack is live and the OpenRouter key is set; skips cleanly otherwise.
