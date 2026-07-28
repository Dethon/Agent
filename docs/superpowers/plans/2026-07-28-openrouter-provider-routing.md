# Per-Agent OpenRouter Provider Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let each agent and subagent declare its own OpenRouter provider-routing policy in `appsettings.json`, replacing the `:nitro` model suffix that is the only routing control today.

**Architecture:** A nullable `ProviderRouting` record on `AgentDefinition`, `SubAgentDefinition` and the global `OpenRouter` config. `MultiAgentFactory` resolves `definition ?? global` and threads the result down the same path `sessionId` already takes — chat-client constructor, `ReasoningHandler`, `PrepareRequestBodyAsync` — where it is stamped onto the request body as `provider`. Unset means no `provider` key at all, which is OpenRouter's balanced load balancing.

**Tech Stack:** .NET 10, `System.Text.Json.Nodes`, `Microsoft.Extensions.Configuration` binding, xUnit + Shouldly + Moq.

**Spec:** `docs/superpowers/specs/2026-07-28-openrouter-provider-routing-design.md`

## Global Constraints

- **`.cs` files carry NO trailing newline.** `.editorconfig` sets `insert_final_newline = false`. This applies to every file created or modified in this plan, tests included.
- **The pre-commit hook re-stages whole files.** `.githooks/pre-commit` runs `dotnet format` over staged `.cs` files and `git add`s them whole. Partial/hunk staging does not survive a commit — make the working tree match the commit you want.
- **Commit on the currently checked-out branch. Never switch branches or create new ones.**
- **Domain layer must not import `Infrastructure` or `Agent` namespaces, or framework types** (`HttpClient`, `JsonNode`, …). `ProviderRouting` and `ProviderRoutingAdvisories` are pure Domain; all JSON mapping lives in Infrastructure.
- **Prefer LINQ over `for`/`foreach`/`while`.** Loops only for unavoidable side effects (the advisory-logging loop qualifies).
- **No XML documentation comments.** Comments explain *why*, never *what*.
- **Tests:** `Shouldly` for assertions, method naming `{Method}_{Scenario}_{ExpectedResult}`.
- **Run tests one filter at a time.** Concurrent `dotnet test` runs in WSL trigger an RCU stall that only `wsl --shutdown` clears.
- **Verification commands:**
  - Build: `dotnet build agent.sln`
  - Test: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~<ClassName>"`

## Target routing after this plan

| Caller | Today | After |
|---|---|---|
| global default | — | balanced (unset) |
| `jack` | balanced | balanced (inherits) |
| `jonas` | throughput | balanced (inherits) — **changed** |
| `nabu` | throughput | latency — **changed** |
| `jonas-worker` | throughput | throughput (explicit) |
| memory extraction / dreaming | balanced | balanced (inherits) |

## File Structure

**Create:**

| File | Responsibility |
|---|---|
| `Domain/DTOs/ProviderRouting.cs` | The `ProviderRouting` record, the `ProviderSort` enum, and `ProviderRoutingAdvisories`. All three are the same concept and change together. |
| `Tests/Unit/Agent/ProviderRoutingBindingTests.cs` | Configuration binding: valid sorts map, typos throw, arrays bind, `IsEmpty` semantics. |
| `Tests/Unit/Domain/ProviderRoutingAdvisoriesTests.cs` | The two advisories, in isolation from any logger. |

**Modify:**

| File | Change |
|---|---|
| `Infrastructure/Agents/ChatClients/OpenRouterHttpHelpers.cs` | New `ProviderRouting?` parameter on `PrepareRequestBodyAsync`; `BuildProviderNode` wire mapper. |
| `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs` | New optional ctor parameter, captured through `CreateHttpClient` into `ReasoningHandler`. |
| `Domain/DTOs/AgentDefinition.cs` | Add `ProviderRouting?`. |
| `Domain/DTOs/SubAgentDefinition.cs` | Add `ProviderRouting?`. |
| `Agent/Settings/AgentSettings.cs` | Add `ProviderRouting?` to `OpenRouterConfiguration`. |
| `Infrastructure/Agents/MultiAgentFactory.cs` | Add `ProviderRouting?` to `OpenRouterConfig`; `ResolveRouting` (precedence + advisory logging); thread into `CreateChatClient`; extend the `chatClientFactory` delegate. |
| `Agent/Modules/InjectorModule.cs` | Carry `ProviderRouting` into `OpenRouterConfig`. |
| `Agent/Modules/MemoryModule.cs` | Bind the global default, pass to both chat clients, drop `:nitro` from the two fallback model strings. |
| `Agent/appsettings.json` | Migrate three model strings; add `providerRouting` to `nabu` and `jonas-worker`. |
| `Tests/Unit/Infrastructure/OpenRouterHttpHelpersTests.cs` | 9 existing call sites gain the new argument; new serialization tests. |
| `Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs` | Precedence and advisory-logging tests. |
| `Tests/Unit/Agent/AgentAppSettingsTests.cs` | Pin the shipped routing table. |
| `CLAUDE.md` | Document provider routing, including that `order` costs the prompt cache. |

---

### Task 1: The `ProviderRouting` record and configuration binding

`ProviderSort` is an enum rather than a string on purpose: .NET configuration binding rejects an unknown value at bind time and names the offending path, which is startup validation with no hand-written validator.

**Files:**
- Create: `Domain/DTOs/ProviderRouting.cs`
- Test: `Tests/Unit/Agent/ProviderRoutingBindingTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Domain.DTOs.ProviderRouting` (record with `ProviderSort? Sort`, `string[]? Order`, `string[]? Only`, `string[]? Ignore`, `bool? AllowFallbacks`, `bool IsEmpty`) and `Domain.DTOs.ProviderSort` (enum: `Price`, `Throughput`, `Latency`). Every later task depends on these exact names.

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Agent/ProviderRoutingBindingTests.cs`:

```csharp
using Domain.DTOs;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.Agent;

// Sort is an enum so a typo fails at bind time instead of shipping an unroutable value to
// OpenRouter. These pin that the binder actually behaves that way -- nothing else would catch
// it, because a bad sort would otherwise only surface as a silently ignored request field.
public class ProviderRoutingBindingTests
{
    [Theory]
    [InlineData("price", ProviderSort.Price)]
    [InlineData("throughput", ProviderSort.Throughput)]
    [InlineData("latency", ProviderSort.Latency)]
    [InlineData("Throughput", ProviderSort.Throughput)]
    public void Bind_ValidSort_MapsToMember(string configured, ProviderSort expected)
    {
        Bind(("providerRouting:sort", configured)).Sort.ShouldBe(expected);
    }

    [Fact]
    public void Bind_InvalidSort_ThrowsNamingThePath()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => Bind(("providerRouting:sort", "cheapest")));

        ex.Message.ShouldContain("providerRouting:sort");
    }

    [Fact]
    public void Bind_ArraysAndFlags_MapFromIndexedKeys()
    {
        var routing = Bind(
            ("providerRouting:order:0", "deepinfra"),
            ("providerRouting:order:1", "novita"),
            ("providerRouting:only:0", "deepinfra"),
            ("providerRouting:ignore:0", "chutes"),
            ("providerRouting:allowFallbacks", "false"));

        routing.Order.ShouldBe(["deepinfra", "novita"]);
        routing.Only.ShouldBe(["deepinfra"]);
        routing.Ignore.ShouldBe(["chutes"]);
        routing.AllowFallbacks.ShouldBe(false);
    }

    [Fact]
    public void Bind_MissingSection_YieldsNull()
    {
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build()
            .GetSection("providerRouting")
            .Get<ProviderRouting>()
            .ShouldBeNull();
    }

    [Fact]
    public void IsEmpty_NoFieldsSet_IsTrue()
    {
        new ProviderRouting().IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void IsEmpty_EmptyArrays_IsTrue()
    {
        new ProviderRouting { Order = [], Only = [], Ignore = [] }.IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(NonEmptyRoutings))]
    public void IsEmpty_AnyFieldSet_IsFalse(string _, ProviderRouting routing)
    {
        routing.IsEmpty.ShouldBeFalse();
    }

    public static IEnumerable<object[]> NonEmptyRoutings =>
    [
        ["sort", new ProviderRouting { Sort = ProviderSort.Price }],
        ["order", new ProviderRouting { Order = ["deepinfra"] }],
        ["only", new ProviderRouting { Only = ["deepinfra"] }],
        ["ignore", new ProviderRouting { Ignore = ["chutes"] }],
        ["allowFallbacks", new ProviderRouting { AllowFallbacks = false }]
    ];

    private static ProviderRouting Bind(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value))
            .Build()
            .GetSection("providerRouting")
            .Get<ProviderRouting>()!;
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ProviderRoutingBindingTests"`

Expected: BUILD FAILURE — `CS0246: The type or namespace name 'ProviderRouting' could not be found`.

- [ ] **Step 3: Write minimal implementation**

Create `Domain/DTOs/ProviderRouting.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs;

// OpenRouter provider-routing preferences, serialized into the request body's `provider`
// object. Every property is nullable so that an unset field is omitted from the wire object
// rather than sent as JSON null -- OpenRouter's balanced load balancing is only available by
// omitting `sort` and `order` entirely, so "absent" has to stay expressible.
[PublicAPI]
public record ProviderRouting
{
    public ProviderSort? Sort { get; init; }
    public string[]? Order { get; init; }
    public string[]? Only { get; init; }
    public string[]? Ignore { get; init; }
    public bool? AllowFallbacks { get; init; }

    public bool IsEmpty =>
        Sort is null &&
        Order is not { Length: > 0 } &&
        Only is not { Length: > 0 } &&
        Ignore is not { Length: > 0 } &&
        AllowFallbacks is null;
}

// An enum rather than a string so configuration binding rejects a typo at bind time, naming
// the offending path, instead of sending a value OpenRouter would silently ignore.
public enum ProviderSort
{
    Price,
    Throughput,
    Latency
}
```

Remember: no trailing newline.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ProviderRoutingBindingTests"`

Expected: PASS, 12 tests.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/ProviderRouting.cs Tests/Unit/Agent/ProviderRoutingBindingTests.cs
git commit -m "feat(routing): add ProviderRouting config record with enum-validated sort"
```

---

### Task 2: Serialize `provider` onto the request body

`PrepareRequestBodyAsync` already stamps `session_id` and `usage`; this adds one more stamp on the same path. The critical behaviour is the negative case: a null or empty routing must produce **no `provider` key at all**, because that absence is what gives OpenRouter's balanced routing.

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/OpenRouterHttpHelpers.cs:13` (signature), `:45` (insertion point, after the `usage` line)
- Modify: `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs:29-45` (public ctor), `:313-341` (`CreateHttpClient`, `ReasoningHandler`)
- Test: `Tests/Unit/Infrastructure/OpenRouterHttpHelpersTests.cs`

**Interfaces:**
- Consumes: `Domain.DTOs.ProviderRouting`, `Domain.DTOs.ProviderSort` from Task 1.
- Produces:
  - `OpenRouterHttpHelpers.PrepareRequestBodyAsync(HttpRequestMessage request, string? sessionId, ProviderRouting? providerRouting, CancellationToken ct)` — the `providerRouting` parameter sits **third**, before `ct`.
  - `internal static JsonObject? OpenRouterHttpHelpers.BuildProviderNode(ProviderRouting? routing)`.
  - `OpenRouterChatClient`'s public constructor gains `ProviderRouting? providerRouting = null` as the **last** parameter, after `timeProvider`. Callers must pass it by name.

- [ ] **Step 1: Update the 9 existing call sites so the file compiles**

In `Tests/Unit/Infrastructure/OpenRouterHttpHelpersTests.cs`, every call currently reads either
`PrepareRequestBodyAsync(request, null, CancellationToken.None)` or
`PrepareRequestBodyAsync(request, "jack:123:456", CancellationToken.None)` or
`PrepareRequestBodyAsync(request, "  ", CancellationToken.None)`.

Insert `null,` before `CancellationToken.None` at all 9 (lines 18, 38, 58, 76, 94, 114, 131, 148, 166):

```csharp
await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, null, CancellationToken.None);
```

```csharp
await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, "jack:123:456", null, CancellationToken.None);
```

```csharp
await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, "  ", null, CancellationToken.None);
```

- [ ] **Step 2: Write the failing tests**

Append these to `Tests/Unit/Infrastructure/OpenRouterHttpHelpersTests.cs`, before the private `CreateRequest` helper:

```csharp
    [Fact]
    public async Task PrepareRequestBody_WithFullProviderRouting_MapsEveryFieldToItsOpenRouterKey()
    {
        // Arrange
        var request = CreateRequest(BodyJson);
        var routing = new ProviderRouting
        {
            Sort = ProviderSort.Throughput,
            Order = ["deepinfra", "novita"],
            Only = ["deepinfra"],
            Ignore = ["chutes"],
            AllowFallbacks = false
        };

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, routing, CancellationToken.None);

        // Assert
        var provider = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["provider"]!;

        provider["sort"]!.GetValue<string>().ShouldBe("throughput");
        provider["order"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["deepinfra", "novita"]);
        provider["only"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["deepinfra"]);
        provider["ignore"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["chutes"]);
        provider["allow_fallbacks"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Theory]
    [InlineData(ProviderSort.Price, "price")]
    [InlineData(ProviderSort.Throughput, "throughput")]
    [InlineData(ProviderSort.Latency, "latency")]
    public async Task PrepareRequestBody_WithSort_SerializesLowercased(ProviderSort sort, string expected)
    {
        // Arrange
        var request = CreateRequest(BodyJson);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(
            request, null, new ProviderRouting { Sort = sort }, CancellationToken.None);

        // Assert
        JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["provider"]!["sort"]!
            .GetValue<string>().ShouldBe(expected);
    }

    [Fact]
    public async Task PrepareRequestBody_WithPartialProviderRouting_OmitsUnsetFields()
    {
        // Arrange
        var request = CreateRequest(BodyJson);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(
            request, null, new ProviderRouting { Sort = ProviderSort.Latency }, CancellationToken.None);

        // Assert
        var provider = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["provider"]!.AsObject();

        provider.Count.ShouldBe(1);
        provider["order"].ShouldBeNull();
        provider["only"].ShouldBeNull();
        provider["ignore"].ShouldBeNull();
        provider["allow_fallbacks"].ShouldBeNull();
    }

    // Balanced load balancing is only available by sending no `sort` and no `order`, so the
    // absence of the whole `provider` key is a behaviour, not an optimisation. This is also the
    // regression guard that today's traffic is unchanged for agents that configure nothing.
    [Fact]
    public async Task PrepareRequestBody_WithNullProviderRouting_OmitsProviderKey()
    {
        // Arrange
        var request = CreateRequest(BodyJson);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, null, CancellationToken.None);

        // Assert
        JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["provider"].ShouldBeNull();
    }

    [Fact]
    public async Task PrepareRequestBody_WithEmptyProviderRouting_OmitsProviderKey()
    {
        // Arrange
        var request = CreateRequest(BodyJson);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(
            request, null, new ProviderRouting(), CancellationToken.None);

        // Assert
        JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["provider"].ShouldBeNull();
    }

    [Fact]
    public async Task PrepareRequestBody_WithEmptyArrays_OmitsThoseKeys()
    {
        // Arrange
        var request = CreateRequest(BodyJson);
        var routing = new ProviderRouting { Sort = ProviderSort.Price, Order = [], Only = [], Ignore = [] };

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, routing, CancellationToken.None);

        // Assert
        var provider = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["provider"]!.AsObject();

        provider.Count.ShouldBe(1);
        provider["sort"]!.GetValue<string>().ShouldBe("price");
    }

    // sort coexists with sticky routing -- only `order` disables it -- so both fields must
    // survive on the same request.
    [Fact]
    public async Task PrepareRequestBody_WithProviderRoutingAndSessionId_KeepsBoth()
    {
        // Arrange
        var request = CreateRequest(BodyJson);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(
            request, "nabu:123:456", new ProviderRouting { Sort = ProviderSort.Latency },
            CancellationToken.None);

        // Assert
        var obj = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;

        obj["session_id"]!.GetValue<string>().ShouldBe("nabu:123:456");
        obj["provider"]!["sort"]!.GetValue<string>().ShouldBe("latency");
        obj["usage"]!["include"]!.GetValue<bool>().ShouldBeTrue();
    }

    private const string BodyJson =
        "{\"model\":\"anthropic/claude-sonnet-4\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
```

Add `using Domain.DTOs;` to the file's using block.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenRouterHttpHelpersTests"`

Expected: BUILD FAILURE — `CS1503` / `CS1501`: no overload of `PrepareRequestBodyAsync` takes four arguments.

- [ ] **Step 4: Add the wire mapper and the new parameter**

In `Infrastructure/Agents/ChatClients/OpenRouterHttpHelpers.cs`, add `using Domain.DTOs;` and change the signature:

```csharp
    public static async Task PrepareRequestBodyAsync(
        HttpRequestMessage request, string? sessionId, ProviderRouting? providerRouting, CancellationToken ct)
```

Immediately after the existing `obj["usage"] = new JsonObject { ["include"] = true };` line, insert:

```csharp
        // Per-agent provider routing. Omitted entirely when unset: OpenRouter's balanced load
        // balancing has no explicit `sort` value and is only reachable by sending no `sort` and
        // no `order` at all.
        if (BuildProviderNode(providerRouting) is { } provider)
        {
            obj["provider"] = provider;
        }
```

Add these two methods to the class:

```csharp
    internal static JsonObject? BuildProviderNode(ProviderRouting? routing)
    {
        if (routing is null || routing.IsEmpty)
        {
            return null;
        }

        var node = new JsonObject();

        if (routing.Sort is { } sort)
        {
            node["sort"] = sort.ToString().ToLowerInvariant();
        }

        AddSlugs(node, "order", routing.Order);
        AddSlugs(node, "only", routing.Only);
        AddSlugs(node, "ignore", routing.Ignore);

        if (routing.AllowFallbacks is { } allowFallbacks)
        {
            node["allow_fallbacks"] = allowFallbacks;
        }

        return node;
    }

    private static void AddSlugs(JsonObject node, string key, string[]? slugs)
    {
        if (slugs is not { Length: > 0 })
        {
            return;
        }

        node[key] = new JsonArray(slugs.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    }
```

- [ ] **Step 5: Thread the routing through the chat client**

In `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs`, append the parameter to the public constructor (last position, after `timeProvider`):

```csharp
    public OpenRouterChatClient(
        string endpoint,
        string apiKey,
        string model,
        int? maxContextTokens = null,
        IMetricsPublisher? metricsPublisher = null,
        string? sessionId = null,
        TimeProvider? timeProvider = null,
        ProviderRouting? providerRouting = null)
```

and change the `_httpClient` assignment inside it:

```csharp
        _httpClient = CreateHttpClient(
            _reasoningQueue, _costQueue, _cachedTokenQueue, sessionId, providerRouting);
```

Update `CreateHttpClient` and `ReasoningHandler`:

```csharp
    private static HttpClient CreateHttpClient(
        ConcurrentQueue<string> reasoningQueue, ConcurrentQueue<decimal> costQueue,
        ConcurrentQueue<long> cachedQueue, string? sessionId, ProviderRouting? providerRouting)
    {
        var handler = new ReasoningHandler(reasoningQueue, costQueue, cachedQueue, sessionId, providerRouting)
        {
            InnerHandler = _sharedHandler
        };
        return new HttpClient(handler, disposeHandler: false);
    }

    private sealed class ReasoningHandler(
        ConcurrentQueue<string> reasoningQueue, ConcurrentQueue<decimal> costQueue,
        ConcurrentQueue<long> cachedQueue, string? sessionId, ProviderRouting? providerRouting)
        : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await OpenRouterHttpHelpers.PrepareRequestBodyAsync(
                request, sessionId, providerRouting, cancellationToken);
            var response = await base.SendAsync(request, cancellationToken);

            if (response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream",
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                response.Content = OpenRouterHttpHelpers.WrapWithReasoningTee(
                    response.Content, reasoningQueue, costQueue, cachedQueue);
            }

            return response;
        }
    }
```

The internal constructor (the one taking `IChatClient innerClient`) is **unchanged** — routing is applied in the HTTP handler, which that constructor does not build.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenRouterHttpHelpersTests"`

Expected: PASS.

Then confirm nothing else broke:

Run: `dotnet build agent.sln`

Expected: Build succeeded. If any other call site fails to compile, it is passing `timeProvider` positionally — switch it to a named argument rather than reordering the parameters.

- [ ] **Step 7: Commit**

```bash
git add Infrastructure/Agents/ChatClients/OpenRouterHttpHelpers.cs \
        Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs \
        Tests/Unit/Infrastructure/OpenRouterHttpHelpersTests.cs
git commit -m "feat(routing): stamp provider routing onto the OpenRouter request body"
```

---

### Task 3: Configuration advisories

Two legal-but-wrong configurations warrant a warning. Both are pure functions of `(model, routing)`, so they live in Domain and are tested without a logger. `For` returns a list rather than a single nullable string so a configuration that trips both reports both.

**Files:**
- Modify: `Domain/DTOs/ProviderRouting.cs` (append `ProviderRoutingAdvisories`)
- Test: `Tests/Unit/Domain/ProviderRoutingAdvisoriesTests.cs`

**Interfaces:**
- Consumes: `ProviderRouting`, `ProviderSort` from Task 1.
- Produces: `Domain.DTOs.ProviderRoutingAdvisories.For(string model, ProviderRouting? routing) -> IReadOnlyList<string>`.

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Domain/ProviderRoutingAdvisoriesTests.cs`:

```csharp
using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Domain;

// Both advisories guard silent failures: a suffix fighting an explicit sort has no documented
// winner, and `order` quietly turns off sticky routing so the prompt cache goes cold every turn.
// Neither shows up in a response, so these tests are the only thing that proves the guards work
// -- after the appsettings migration nothing in the shipped configuration triggers either one.
public class ProviderRoutingAdvisoriesTests
{
    [Theory]
    [InlineData("z-ai/glm-5.2:nitro", ProviderSort.Price)]
    [InlineData("z-ai/glm-5.2:nitro", ProviderSort.Latency)]
    [InlineData("z-ai/glm-5.2:floor", ProviderSort.Throughput)]
    [InlineData("z-ai/glm-5.2:floor", ProviderSort.Latency)]
    public void For_SuffixDisagreesWithSort_ReturnsOneAdvisory(string model, ProviderSort sort)
    {
        var advisories = ProviderRoutingAdvisories.For(model, new ProviderRouting { Sort = sort });

        advisories.Count.ShouldBe(1);
        advisories[0].ShouldContain(model);
    }

    [Theory]
    [InlineData("z-ai/glm-5.2:nitro", ProviderSort.Throughput)]
    [InlineData("z-ai/glm-5.2:floor", ProviderSort.Price)]
    public void For_SuffixAgreesWithSort_ReturnsNothing(string model, ProviderSort sort)
    {
        ProviderRoutingAdvisories.For(model, new ProviderRouting { Sort = sort }).ShouldBeEmpty();
    }

    [Fact]
    public void For_SuffixWithNonSortFieldsOnly_ReturnsNothing()
    {
        var routing = new ProviderRouting
        {
            Only = ["deepinfra"],
            Ignore = ["chutes"],
            AllowFallbacks = false
        };

        ProviderRoutingAdvisories.For("z-ai/glm-5.2:nitro", routing).ShouldBeEmpty();
    }

    [Fact]
    public void For_NoSuffixWithSort_ReturnsNothing()
    {
        ProviderRoutingAdvisories
            .For("z-ai/glm-5.2", new ProviderRouting { Sort = ProviderSort.Price })
            .ShouldBeEmpty();
    }

    [Fact]
    public void For_NullRouting_ReturnsNothing()
    {
        ProviderRoutingAdvisories.For("z-ai/glm-5.2:nitro", null).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("z-ai/glm-5.2")]
    [InlineData("z-ai/glm-5.2:nitro")]
    public void For_OrderSet_WarnsAboutStickyRouting(string model)
    {
        var advisories = ProviderRoutingAdvisories.For(
            model, new ProviderRouting { Order = ["deepinfra"] });

        advisories.ShouldContain(a => a.Contains("sticky routing"));
    }

    [Fact]
    public void For_EmptyOrder_ReturnsNothing()
    {
        ProviderRoutingAdvisories
            .For("z-ai/glm-5.2", new ProviderRouting { Order = [] })
            .ShouldBeEmpty();
    }

    // Proves the helper does not stop at the first match.
    [Fact]
    public void For_SuffixConflictAndOrder_ReturnsBothAdvisories()
    {
        var routing = new ProviderRouting { Sort = ProviderSort.Price, Order = ["deepinfra"] };

        var advisories = ProviderRoutingAdvisories.For("z-ai/glm-5.2:nitro", routing);

        advisories.Count.ShouldBe(2);
        advisories.ShouldContain(a => a.Contains(":nitro"));
        advisories.ShouldContain(a => a.Contains("sticky routing"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ProviderRoutingAdvisoriesTests"`

Expected: BUILD FAILURE — `CS0103: The name 'ProviderRoutingAdvisories' does not exist`.

- [ ] **Step 3: Write minimal implementation**

Append to `Domain/DTOs/ProviderRouting.cs` (after the `ProviderSort` enum):

```csharp
// Two routing configurations are legal, silent, and almost certainly not what the author meant.
// Neither is visible in a response, so they are reported at agent construction instead.
public static class ProviderRoutingAdvisories
{
    private static readonly (string Suffix, ProviderSort Sort)[] _suffixSorts =
    [
        (":nitro", ProviderSort.Throughput),
        (":floor", ProviderSort.Price)
    ];

    public static IReadOnlyList<string> For(string model, ProviderRouting? routing)
    {
        return routing is null
            ? []
            : new[] { SuffixConflict(model, routing), StickyRoutingLoss(routing) }
                .OfType<string>()
                .ToList();
    }

    private static string? SuffixConflict(string model, ProviderRouting routing)
    {
        if (routing.Sort is not { } sort)
        {
            return null;
        }

        var match = _suffixSorts.FirstOrDefault(
            s => model.EndsWith(s.Suffix, StringComparison.OrdinalIgnoreCase));

        return match.Suffix is not null && match.Sort != sort
            ? $"model '{model}' carries the '{match.Suffix}' suffix, which means sort "
              + $"'{Name(match.Sort)}', but providerRouting.sort is '{Name(sort)}'. OpenRouter "
              + "does not document which wins -- remove one."
            : null;
    }

    private static string? StickyRoutingLoss(ProviderRouting routing)
    {
        return routing.Order is { Length: > 0 }
            ? "providerRouting.order disables OpenRouter sticky routing, so the session_id on "
              + "each request is ignored and the prompt cache goes cold every turn. Use 'only' "
              + "with 'sort' to restrict the provider set without that cost."
            : null;
    }

    private static string Name(ProviderSort sort) => sort.ToString().ToLowerInvariant();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ProviderRoutingAdvisoriesTests"`

Expected: PASS, 15 tests.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/ProviderRouting.cs Tests/Unit/Domain/ProviderRoutingAdvisoriesTests.cs
git commit -m "feat(routing): warn on suffix/sort conflicts and prompt-cache-killing order"
```

---

### Task 4: Per-definition configuration and precedence

Precedence is wholesale replacement — an agent that sets `providerRouting` owns the whole object and inherits no individual fields.

**Files:**
- Modify: `Domain/DTOs/AgentDefinition.cs`, `Domain/DTOs/SubAgentDefinition.cs`
- Modify: `Agent/Settings/AgentSettings.cs:15-20` (`OpenRouterConfiguration`)
- Modify: `Infrastructure/Agents/MultiAgentFactory.cs:22` (delegate), `:53` and `:100` (call sites), `:163-181` (`CreateChatClient`), `:184-189` (`OpenRouterConfig`)
- Modify: `Agent/Modules/InjectorModule.cs:23-28`
- Test: `Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs`

**Interfaces:**
- Consumes: `ProviderRouting`, `ProviderSort`, `ProviderRoutingAdvisories` from Tasks 1 and 3; `OpenRouterChatClient`'s `providerRouting:` named parameter from Task 2.
- Produces:
  - `AgentDefinition.ProviderRouting`, `SubAgentDefinition.ProviderRouting`, `OpenRouterConfiguration.ProviderRouting`, `OpenRouterConfig.ProviderRouting` — all `ProviderRouting?`.
  - `MultiAgentFactory`'s `chatClientFactory` delegate becomes `Func<string, int?, IMetricsPublisher?, ProviderRouting?, IChatClient>?`.

- [ ] **Step 1: Write the failing tests**

Append to `Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs`, inside the class:

```csharp
    [Fact]
    public void Create_AgentDeclaresRouting_UsesItsOwnAndNotTheGlobalDefault()
    {
        var agentRouting = new ProviderRouting { Sort = ProviderSort.Latency };
        var (factory, captured, _) = CreateCapturingFactory(
            new ProviderRouting { Sort = ProviderSort.Throughput },
            RoutedAgent("routed", agentRouting));

        factory.Create(new AgentKey("1:1", "test"), "user1", "routed", _approvalHandler.Object);

        captured.Single().ShouldBe(agentRouting);
    }

    [Fact]
    public void Create_AgentDeclaresNoRouting_InheritsTheGlobalDefault()
    {
        var globalRouting = new ProviderRouting { Sort = ProviderSort.Throughput };
        var (factory, captured, _) = CreateCapturingFactory(globalRouting, RoutedAgent("plain", null));

        factory.Create(new AgentKey("1:1", "test"), "user1", "plain", _approvalHandler.Object);

        captured.Single().ShouldBe(globalRouting);
    }

    // Balanced routing is the absence of a provider object, so "neither set" must resolve to
    // null rather than to some empty-but-present default.
    [Fact]
    public void Create_NeitherAgentNorGlobalDeclaresRouting_ResolvesToNull()
    {
        var (factory, captured, _) = CreateCapturingFactory(null, RoutedAgent("plain", null));

        factory.Create(new AgentKey("1:1", "test"), "user1", "plain", _approvalHandler.Object);

        captured.Single().ShouldBeNull();
    }

    [Fact]
    public void CreateSubAgent_DeclaresRouting_UsesItsOwnAndNotTheGlobalDefault()
    {
        var subRouting = new ProviderRouting { Sort = ProviderSort.Throughput };
        var (factory, captured, _) = CreateCapturingFactory(
            new ProviderRouting { Sort = ProviderSort.Price });

        factory.CreateSubAgent(
            RoutedSubAgent(subRouting), _approvalHandler.Object, [], "user1");

        captured.Single().ShouldBe(subRouting);
    }

    [Fact]
    public void CreateSubAgent_DeclaresNoRouting_InheritsTheGlobalDefault()
    {
        var globalRouting = new ProviderRouting { Sort = ProviderSort.Price };
        var (factory, captured, _) = CreateCapturingFactory(globalRouting);

        factory.CreateSubAgent(RoutedSubAgent(null), _approvalHandler.Object, [], "user1");

        captured.Single().ShouldBe(globalRouting);
    }

    [Fact]
    public void Create_RoutingTripsAnAdvisory_LogsAWarningNamingTheAgent()
    {
        var routing = new ProviderRouting { Order = ["deepinfra"] };
        var (factory, _, logs) = CreateCapturingFactory(null, RoutedAgent("noisy", routing));

        factory.Create(new AgentKey("1:1", "test"), "user1", "noisy", _approvalHandler.Object);

        logs.ShouldContain(m => m.Contains("noisy") && m.Contains("sticky routing"));
    }

    // Asserts the absence of an advisory rather than an empty log: agent construction may warn
    // about unrelated things, and this test must not become a tripwire for those.
    [Fact]
    public void Create_RoutingIsClean_LogsNoAdvisory()
    {
        var routing = new ProviderRouting { Sort = ProviderSort.Latency };
        var (factory, _, logs) = CreateCapturingFactory(null, RoutedAgent("quiet", routing));

        factory.Create(new AgentKey("1:1", "test"), "user1", "quiet", _approvalHandler.Object);

        logs.ShouldNotContain(m => m.Contains("sticky routing") || m.Contains("providerRouting.sort"));
    }

    private static AgentDefinition RoutedAgent(string id, ProviderRouting? routing) => new()
    {
        Id = id,
        Name = id,
        Model = "z-ai/glm-5.2",
        McpServerEndpoints = [],
        ProviderRouting = routing
    };

    private static SubAgentDefinition RoutedSubAgent(ProviderRouting? routing) => new()
    {
        Id = "worker",
        Name = "Worker",
        Model = "z-ai/glm-5.2",
        McpServerEndpoints = [],
        ProviderRouting = routing
    };

    private (MultiAgentFactory Factory, List<ProviderRouting?> Captured, List<string> Logs)
        CreateCapturingFactory(ProviderRouting? globalRouting, params AgentDefinition[] agents)
    {
        var captured = new List<ProviderRouting?>();
        var logProvider = new CapturingLoggerProvider();

        var optionsMonitor = new Mock<IOptionsMonitor<AgentRegistryOptions>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(new AgentRegistryOptions { Agents = agents });

        var domainToolRegistry = new Mock<IDomainToolRegistry>();
        domainToolRegistry
            .Setup(r => r.GetToolsForFeatures(It.IsAny<IEnumerable<string>>(), It.IsAny<FeatureConfig>()))
            .Returns(Enumerable.Empty<AIFunction>());
        domainToolRegistry
            .Setup(r => r.GetPromptsForFeatures(It.IsAny<IEnumerable<string>>()))
            .Returns(Enumerable.Empty<string>());

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IThreadStateStore)))
            .Returns(new Mock<IThreadStateStore>().Object);

        var factory = new MultiAgentFactory(
            serviceProvider.Object,
            new AgentDefinitionProvider(optionsMonitor.Object, new CustomAgentRegistry()),
            new OpenRouterConfig
            {
                ApiUrl = "http://test",
                ApiKey = "test-key",
                ProviderRouting = globalRouting
            },
            domainToolRegistry.Object,
            loggerFactory: LoggerFactory.Create(b => b.AddProvider(logProvider)),
            chatClientFactory: (_, _, _, routing) =>
            {
                captured.Add(routing);
                return new Mock<IChatClient>().Object;
            });

        return (factory, captured, logProvider.Messages);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                {
                    messages.Add(formatter(state, exception));
                }
            }
        }
    }
```

Add `using Microsoft.Extensions.Logging;` to the file's using block.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MultiAgentFactoryTests"`

Expected: BUILD FAILURE — `CS0117: 'AgentDefinition' does not contain a definition for 'ProviderRouting'`, plus a delegate arity error on the `chatClientFactory` lambda.

- [ ] **Step 3: Add the properties**

`Domain/DTOs/AgentDefinition.cs` — add after `ReasoningEffort`:

```csharp
    public ProviderRouting? ProviderRouting { get; init; }
```

`Domain/DTOs/SubAgentDefinition.cs` — add after `ReasoningEffort`:

```csharp
    public ProviderRouting? ProviderRouting { get; init; }
```

`Agent/Settings/AgentSettings.cs` — add to `OpenRouterConfiguration` after `MaxContextTokens`:

```csharp
    public ProviderRouting? ProviderRouting { get; [UsedImplicitly] init; }
```

`Infrastructure/Agents/MultiAgentFactory.cs` — add to `OpenRouterConfig` after `MaxContextTokens`:

```csharp
    public ProviderRouting? ProviderRouting { get; init; }
```

`Agent/Modules/InjectorModule.cs` — extend the `llmConfig` initializer:

```csharp
            var llmConfig = new OpenRouterConfig
            {
                ApiUrl = settings.OpenRouter.ApiUrl,
                ApiKey = settings.OpenRouter.ApiKey,
                MaxContextTokens = settings.OpenRouter.MaxContextTokens,
                ProviderRouting = settings.OpenRouter.ProviderRouting
            };
```

- [ ] **Step 4: Add resolution and advisory logging to `MultiAgentFactory`**

Change the constructor's delegate parameter:

```csharp
    Func<string, int?, IMetricsPublisher?, ProviderRouting?, IChatClient>? chatClientFactory = null)
```

Add this method to the class:

```csharp
    // Wholesale replacement, not a per-field merge: an agent that declares routing owns the
    // whole object, so it can never inherit an `ignore` list invisible at its own config site.
    private ProviderRouting? ResolveRouting(string agentId, string model, ProviderRouting? declared)
    {
        var effective = declared ?? openRouterConfig.ProviderRouting;
        var logger = loggerFactory?.CreateLogger<MultiAgentFactory>();

        if (logger is null)
        {
            return effective;
        }

        foreach (var advisory in ProviderRoutingAdvisories.For(model, effective))
        {
            logger.LogWarning("Agent '{AgentId}': {Advisory}", agentId, advisory);
        }

        return effective;
    }
```

Change `CreateChatClient` to accept an already-resolved routing and pass it on — it must **not** apply the `??` fallback a second time:

```csharp
    private IChatClient CreateChatClient(
        string model, IMetricsPublisher? publisher = null, int? maxContextTokens = null,
        string? sessionId = null, ProviderRouting? providerRouting = null)
    {
        var effectivePublisher = publisher ?? metricsPublisher;
        var effectiveContext = maxContextTokens ?? openRouterConfig.MaxContextTokens;

        if (chatClientFactory is not null)
        {
            return chatClientFactory(model, effectiveContext, effectivePublisher, providerRouting);
        }

        return new OpenRouterChatClient(
            openRouterConfig.ApiUrl,
            openRouterConfig.ApiKey,
            model,
            effectiveContext,
            effectivePublisher,
            sessionId,
            providerRouting: providerRouting);
    }
```

`providerRouting` must be passed **by name** because `timeProvider` sits between it and `sessionId`.

In `CreateSubAgent`, replace the `chatClient` assignment:

```csharp
        var chatClient = CreateChatClient(
            definition.Model, agentPublisher, definition.MaxContextTokens,
            sessionId: $"subagent-{definition.Id}:{Guid.NewGuid():N}",
            providerRouting: ResolveRouting(
                $"subagent-{definition.Id}", definition.Model, definition.ProviderRouting));
```

In `CreateFromDefinition`, replace the `chatClient` assignment:

```csharp
        var chatClient = CreateChatClient(
            definition.Model, agentPublisher, definition.MaxContextTokens,
            sessionId: $"{definition.Id}:{agentKey.ConversationId}",
            providerRouting: ResolveRouting(definition.Id, definition.Model, definition.ProviderRouting));
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MultiAgentFactoryTests"`

Expected: PASS — the 7 new tests plus the pre-existing `Create_SupportedAgentIdentifier_ReturnsAgent` and `Create_RejectsInvalidAgentId_Throws` theories.

Run: `dotnet build agent.sln`

Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add Domain/DTOs/AgentDefinition.cs Domain/DTOs/SubAgentDefinition.cs \
        Agent/Settings/AgentSettings.cs Agent/Modules/InjectorModule.cs \
        Infrastructure/Agents/MultiAgentFactory.cs \
        Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs
git commit -m "feat(routing): resolve per-agent provider routing with a global default"
```

---

### Task 5: Memory models, the appsettings migration, and docs

This is where behaviour actually changes: `jonas` moves throughput → balanced and `nabu` moves throughput → latency. Everything else keeps sending exactly what it sends today.

**Files:**
- Modify: `Agent/Modules/MemoryModule.cs:37-66`
- Modify: `Agent/appsettings.json:61`, `:90`, `:123`
- Modify: `Tests/Unit/Agent/AgentAppSettingsTests.cs`
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces: the shipped configuration pinned by the target-routing table.

- [ ] **Step 1: Write the failing tests**

Append to `Tests/Unit/Agent/AgentAppSettingsTests.cs`, inside the class:

```csharp
    // Sort choices are deliberate per-agent decisions that nothing else would catch if reverted.
    // Nabu is the voice agent: time-to-first-token gates when speech starts, which is what
    // `latency` sorts on, where `throughput` sorts on sustained tokens/second -- the wrong metric
    // for replies capped at one short sentence.
    [Fact]
    public void ProviderRouting_Nabu_SortsByLatency()
    {
        Agent("nabu")["providerRouting"]!["sort"]!.GetValue<string>().ShouldBe("latency");
    }

    [Fact]
    public void ProviderRouting_JonasWorker_SortsByThroughput()
    {
        SubAgent("jonas-worker")["providerRouting"]!["sort"]!.GetValue<string>().ShouldBe("throughput");
    }

    // Balanced routing is the absence of a provider object -- there is no `sort` value for it --
    // so it can only be asserted as an absence, never read back off a request.
    [Theory]
    [InlineData("jack")]
    [InlineData("jonas")]
    public void ProviderRouting_BalancedAgents_DeclareNone(string agentId)
    {
        Agent(agentId)["providerRouting"].ShouldBeNull();
    }

    // One line added here would move every non-overriding caller -- Jack, Jonas and both memory
    // models -- off load balancing at once, silently.
    [Fact]
    public void ProviderRouting_GlobalDefault_IsUnset()
    {
        Root()["openRouter"]!["providerRouting"].ShouldBeNull();
    }

    // The migration exists to remove the dual-idiom problem; a pasted suffix would bring it back.
    [Fact]
    public void Model_NoAgentOrSubAgent_CarriesARoutingSuffix()
    {
        var models = Root()["agents"]!.AsArray()
            .Concat(Root()["subAgents"]!.AsArray())
            .Select(a => a!["model"]!.GetValue<string>());

        foreach (var model in models)
        {
            model.ShouldNotContain(":nitro");
            model.ShouldNotContain(":floor");
        }
    }

    private static JsonNode SubAgent(string subAgentId) =>
        Root()["subAgents"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == subAgentId)!;

    private static JsonNode Root()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Agent", "appsettings.json"));
        return JsonNode.Parse(json)!;
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AgentAppSettingsTests"`

Expected: FAIL. `ProviderRouting_Nabu_SortsByLatency` throws `NullReferenceException` (no `providerRouting` key yet), and `Model_NoAgentOrSubAgent_CarriesARoutingSuffix` fails on `z-ai/glm-5.2:nitro`.

- [ ] **Step 3: Migrate `Agent/appsettings.json`**

Agent `jonas` (line 61) — drop the suffix, add nothing:

```json
            "model": "z-ai/glm-5.2",
```

Agent `nabu` (line 90) — drop the suffix and add routing immediately after the `model` line:

```json
            "model": "z-ai/glm-5.2",
            "providerRouting": {
                "sort": "latency"
            },
```

Subagent `jonas-worker` (line 123) — same shape:

```json
            "model": "z-ai/glm-5.2",
            "providerRouting": {
                "sort": "throughput"
            },
```

Leave the `openRouter` section alone — the global default ships unset.

Note this file uses 4-space indentation and carries a UTF-8 BOM; preserve both.

- [ ] **Step 4: Wire the global default into the memory models**

In `Agent/Modules/MemoryModule.cs`, add `using Domain.DTOs;` to the using block.

In the `IMemoryExtractor` registration, after the `openRouterConfig` line:

```csharp
                var openRouterConfig = config.GetSection("openRouter");
                var providerRouting = openRouterConfig.GetSection("providerRouting").Get<ProviderRouting>();
                var extractionModel = memoryConfig["Extraction:Model"] ?? "z-ai/glm-4.7-flash";
                var metricsPublisher = sp.GetRequiredService<IMetricsPublisher>();
                var chatClient = new OpenRouterChatClient(
                    openRouterConfig["apiUrl"]!, openRouterConfig["apiKey"]!,
                    extractionModel,
                    maxContextTokens: openRouterConfig.GetValue<int?>("maxContextTokens"),
                    metricsPublisher: metricsPublisher,
                    providerRouting: providerRouting);
```

In the `IMemoryConsolidator` registration, the same shape:

```csharp
                var openRouterConfig = config.GetSection("openRouter");
                var providerRouting = openRouterConfig.GetSection("providerRouting").Get<ProviderRouting>();
                var dreamingModel = memoryConfig["Dreaming:Model"] ?? "z-ai/glm-4.7-flash";
                var metricsPublisher = sp.GetRequiredService<IMetricsPublisher>();
                var chatClient = new OpenRouterChatClient(
                    openRouterConfig["apiUrl"]!, openRouterConfig["apiKey"]!,
                    dreamingModel,
                    maxContextTokens: openRouterConfig.GetValue<int?>("maxContextTokens"),
                    metricsPublisher: metricsPublisher,
                    providerRouting: providerRouting);
```

Both `:nitro` suffixes are dropped from the fallback strings. These fallbacks are inert today — `appsettings.json` sets both models to `openai/gpt-5.4-nano` — so this changes nothing at runtime; it removes the last two places the old idiom could be copied from.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AgentAppSettingsTests"`

Expected: PASS.

- [ ] **Step 6: Document it in `CLAUDE.md`**

Insert a new section immediately after the `## Environment Variables` section and before `## Multi-Agent Patterns`:

```markdown
## OpenRouter Provider Routing

Each `agents[]` / `subAgents[]` entry may carry a `providerRouting` object (`sort` ∈
`price`|`throughput`|`latency`, plus `order`, `only`, `ignore`, `allowFallbacks`), overriding
`openRouter.providerRouting` **wholesale** — never field-by-field. It reaches the wire through
the same path as `session_id`: `MultiAgentFactory.ResolveRouting` → `OpenRouterChatClient` →
`ReasoningHandler` → `OpenRouterHttpHelpers.PrepareRequestBodyAsync`, which stamps `provider`.

**Balanced routing is the absence of the object.** OpenRouter has no `sort` value for its
default load balancing (uptime filter, then inverse-square price weighting) — it is only
reachable by sending neither `sort` nor `order`, so the global default ships unset and
`AgentAppSettingsTests` pins it that way. `sort: "price"` is a different thing: deterministically
the cheapest provider, not a weighted spread.

**`order` costs the prompt cache.** Sticky routing — the reason every request carries a
`session_id` — is disabled when `provider.order` is set, so the ~17k-token static prefix is
re-sent uncached every turn. `sort` does *not* disable it. Prefer `only` + `sort` to restrict
the provider set. `ProviderRoutingAdvisories` logs a warning for this and for a `:nitro`/`:floor`
model suffix fighting an explicit `sort`; both are warnings, never throws, because the same path
serves runtime-created agents.

Current: `nabu` latency, `jonas-worker` throughput, everything else balanced.
```

- [ ] **Step 7: Run the full unit suite**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit"`

Expected: PASS. Judge failures by type, not count — a pre-existing McpAgent cleanup failure in the integration suite is a known baseline and is not in this filter.

Run: `dotnet build agent.sln`

Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add Agent/Modules/MemoryModule.cs Agent/appsettings.json \
        Tests/Unit/Agent/AgentAppSettingsTests.cs CLAUDE.md
git commit -m "feat(routing): migrate agents off :nitro onto explicit provider routing"
```

---

## Deployment note

This changes `Agent/appsettings.json`, which is baked into the `agent` image. The routing change reaches production only after `mcp`-stack rebuild and redeploy of the `agent` service — there is no runtime reload path for `agents[]`.

The dashboard's by-model token/latency series split at the deploy boundary: `TokenUsageEvent`/`ContextTruncationEvent` carry the config model string verbatim, so jonas, nabu and jonas-worker history keyed on `z-ai/glm-5.2:nitro` flatlines and a new bare-slug series starts. Not a traffic or model change — the discontinuity self-heals as the old series ages past the 30-day metric TTL.
