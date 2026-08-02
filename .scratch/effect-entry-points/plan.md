# Effect Entry Points Implementation Plan

**Goal:** Give every WebChat effect an awaitable entry point so its behaviour is reachable from a test, and stop first-load failures from vanishing.

**Why now:** Seven effects have no test at all, including `InitializationEffect` — 214 lines owning the entire first-load ordering (connect → subscribe → register user → validate space → join → load agents → load settings → select agent → load topics → per-topic history + stream resume). Their whole contract is a constructor side effect, they fire-and-forget with no completion signal, and `InitializationEffect` takes 14 dependencies of which two are concrete classes with JS-interop, so it cannot be constructed in a unit test at all.

**Source:** architecture review 2026-08-02, candidate 9.

## Global Constraints

- TDD per task. `dotnet test Tests/Unit --nologo -v q`.
- `.cs` files have no trailing newline; pre-commit runs `dotnet format` and re-stages whole files.
- File-scoped namespaces, primary constructors, no XML doc comments.
- Commit after each task.

## Depends on

`.scratch/webchat-slice-shape/plan.md`. That plan changes `Dispatcher` and `Store.Dispatch`; land it first so effect tests are written against the final dispatch semantics.

## Locked decisions

**Public `HandleAsync`, constructor wraps it.** `Dispatch` stays `void` across all 131 call sites — Flux dispatch is fire-and-forget by design, and a button dispatching `SelectTopic` has no business blocking on history loading. The testability problem is precisely two things, no addressable input and no completion signal, and a public awaitable member fixes both without touching anything outside the effect.

```csharp
public Task HandleInitializeAsync(CancellationToken ct = default);

// ctor
dispatcher.RegisterHandler<Initialize>(_ => HandleInitializeAsync().LogFaults(logger));
```

Registration stays in the constructor, next to the handler, where it cannot drift. A composition-root wiring table was rejected: the slice-shape plan deletes ten such tables precisely because a forgotten entry fails silently.

**The wrapper logs faults.** `_ = HandleInitializeAsync()` at `InitializationEffect.cs:67` swallows any throw during first-load init today — no log, no toast, the app sits half-initialized. This is a live bug closed as a side effect.

**Effects get no interfaces of their own.** They are instantiated eagerly and injected into nothing, so there is no consumer to abstract. ADR-0001's convention covers injected dependencies, which effects are not.

**`IConfigService` and `IPushNotificationService` are added.** These are the only two of `InitializationEffect`'s 14 dependencies that are concrete, and `PushNotificationService(IJSRuntime, IChatConnectionService)` is what makes the effect unconstructable without a Blazor JS runtime. Extracting them follows directly from ADR-0001's uniformity rationale — every injected dependency reached through an interface.

## Tasks

1. **`IConfigService`, `IPushNotificationService`.** Extract from the concrete classes, register both in `Program.cs`.
2. **`LogFaults` helper** on `Task`, plus a test that a throwing handler logs rather than disappearing.
3. **Convert the seven untested effects** to public `HandleAsync` + wrapping registration: `InitializationEffect` (214 lines), `TopicSelectionEffect` (104), `AgentSelectionEffect` (95), `SpaceEffect` (81), `AgentActivityEffect` (79), `TopicDeleteEffect` (71), `UserIdentityEffect` (64). One commit each.
4. **Test `InitializationEffect`'s ordering.** This is the deliverable: the 214-line first-load sequence, asserted without a browser.
5. **Reshape `AgentSelectionEffect`'s trigger.** It fires on an Rx diff of `TopicsStore.StateObservable` against a private `_previousAgentId` (`:41,46,53`), so it has no addressable input and a public `HandleAsync` alone does not fix it. Either dispatch an explicit action on agent change, or expose the diff as a parameter. This is a separate design call — flag it rather than guessing.

## Risks

- **`InitializationEffect` reads `_spaceStore.State.CurrentSlug` immediately after dispatching `InvalidSpace`** (`:87-88`), relying on stores being registered before effects in `Program.cs:35-36`. A test constructing the effect directly must reproduce that ordering or it will assert against stale state. The slice-shape plan pins the ordering with a test; this plan depends on it.
- Task 5 is genuinely undecided. Do not fold it into task 3.
- Seven effects converted mechanically is where a subtle behaviour change hides. Each conversion should be a no-op diff apart from the signature and the fault log.
