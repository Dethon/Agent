# Resilient `text_create` / `text_edit`: tolerate JSON-object misuse of string fields

**Date:** 2026-07-15
**Status:** Proposed — awaiting review

## Problem

The agent tried to create `/schedules/<id>/schedule.json` by calling `text_create`
with `content` set to a JSON **object** (`{"cron": "...", "prompt": "..."}`) instead of a
JSON **string** containing that object's text. The `content` parameter is typed `string`,
so `System.Text.Json` throws at **argument-binding time** — before the tool body runs —
and `FunctionInvokingChatClient` hands the model a generic "the function failed" error
(detailed errors are off by default). The model gets an unhelpful failure and has to guess
that it should stringify, costing at least one wasted round-trip (and sometimes a failure
loop). The same failure mode exists on `text_edit`'s `oldString` / `newString`.

For a `*.json` file this misuse is especially natural: the object the model passed *is*
exactly what belongs in the file. We want the tool to accept it and do the right thing.

## Goals

- `text_create.content` and `text_edit.oldString` / `newString` accept a JSON object,
  array, number, boolean, or null where a string is expected, and **coerce it to text**
  instead of failing to bind.
- When coercion happens, the call **succeeds** and the result **surfaces a note** so the
  behavior is observable to the model and in telemetry.
- Behavior is **identical across every mounted filesystem** (vault, schedules, library,
  sandbox, printer, HA, …) — no per-backend divergence.
- The tools' advertised JSON **schema for these fields stays `"type": "string"`** so the
  model is still steered to pass strings by default (coercion is a safety net, not the
  happy path).

## Non-goals

- `filePath` (and glob/search/other string params) stay **strict**. An object passed for a
  path is a genuine error and should fail loudly rather than coerce to a garbage path.
- No change to the domain `TextEdit` DTO or to any `IFileSystemBackend` implementation.
- No new environment variables or configuration.

## Behavior specification

Coercion rule applied to `content`, `oldString`, `newString`:

| Model sends           | Value written / used              | Coerced? (note) |
|-----------------------|-----------------------------------|-----------------|
| `"…"` (JSON string)   | the string as-is                  | no              |
| `{…}` / `[…]`         | its JSON text (raw, model's own formatting preserved) | **yes** |
| number / boolean      | its literal text (`5`, `true`)    | **yes**         |
| null / missing        | empty string                      | **yes**         |

- Object/array text is taken verbatim from the incoming argument (`JsonElement.GetRawText()`),
  so the file contains exactly what the model sent — no re-serialization / reformatting.
- `filePath` is unaffected: still bound strictly as `string`.

### The note

When coercion fires and the backend call succeeds, the success envelope carries an optional
`note` field, e.g.:

```json
{ "ok": true, "status": "created", "filePath": "/schedules/x/schedule.json",
  "size": "…", "lines": 1,
  "note": "content arrived as a JSON object and was written as its JSON text; pass it as a string to avoid this." }
```

The note is added **only** when coercion happened. Non-coerced calls are byte-identical to
today's output.

## Design

### Chokepoint (this is what makes it cross-filesystem-consistent)

Both tools dispatch to *every* backend through a single method each:

- `VfsTextCreateTool.RunAsync` → `registry.Resolve(path).Backend.CreateAsync(...)`
- `VfsTextEditTool.RunAsync`   → `registry.Resolve(path).Backend.EditAsync(...)`

All coercion and note logic lives in these two tool bodies, **above** the registry. Because
resolution/dispatch happens *after*, every mount receives the already-coerced text and the
identical note. There is no place for per-backend divergence, and no backend changes.

### Keeping the schema `"string"` — per-parameter lenient binding

The parameter types stay exactly what they are today — `string content` and
`IReadOnlyList<TextEdit> edits` — so `AIFunctionFactory` generates their JSON schema from the
real CLR types (`{"type":"string"}`, plus the existing `[Description]`s). Nothing rewrites the
schema; it is correct by construction. This is the safest way to honor the "keep the string"
requirement: the field genuinely *is* a string parameter.

What changes is only **how** those parameters are bound from the raw arguments. In
`FileSystemToolFeature.GetTools`, the two tools are created with an
`AIFunctionFactoryOptions` whose `ConfigureParameterBinding` installs a lenient
`BindParameter` for the lenient fields only:

- `content` → a binder that reads the raw argument and returns coerced text (never throws).
- `edits`   → a binder that parses the raw array, coercing each element's `oldString` /
  `newString`, and returns a `IReadOnlyList<TextEdit>` of **domain** `TextEdit` (backends
  keep seeing plain strings).
- `filePath` and every other parameter → **no** custom binder ⇒ default strict binding.

A single `TextArg` helper (Domain) holds the coercion logic:
`Coerce(rawArgument) → (string Text, bool Coerced)`, reused by both binders and unit-tested
directly against each `JsonValueKind`.

### Surfacing the note without breaking envelope validation

Success payloads are validated at the agent/MCP boundary through
`FsResultContract.ValidationOptions`, which sets `UnmappedMemberHandling.Disallow` — an
unknown member fails validation. So the note must be a **first-class, optional** member of the
result contract, not a stray field:

- Add `public string? Note { get; init; }` to `FsCreateResult` and `FsEditResult`.
- It is nullable and non-`required`; `FsResultContract.SerializerOptions` already sets
  `DefaultIgnoreCondition = WhenWritingNull`, so it is **omitted** unless set — existing
  outputs and remote-server payloads are unchanged, and validation still passes (a *missing*
  optional member is fine; only *unknown* members fail).

The backend still builds the `FsCreateResult` / `FsEditResult` (it knows nothing about
coercion). The tool body, when it detected coercion, rebuilds the typed value with the note
and serializes that:

```csharp
if (coerced && result.TryGetValue(out var ok, out _))
    return FsResultContract.ToNode(ok with { Note = CoercionNote });
return result.ToNode();
```

Type-safe, validated, no raw `JsonNode` surgery.

### How the tool body learns coercion happened

The binder produces the coerced value; the body needs the boolean to decide the note. The
body takes an injected `AIFunctionArguments` parameter (a standard M.E.AI injection, excluded
from the schema) and derives coercion from the raw argument it still holds (object/array/
number/bool/null ⇒ coerced). Coercion logic is not duplicated — it is the same `TextArg`
predicate used by the binder.

## Key risk to validate first

The note path depends on the tool body being able to observe, via the injected
`AIFunctionArguments`, that the raw `content` / `edits` argument was structured. This is
standard M.E.AI behavior but is an internal detail, so the **first** test written is the
end-to-end regression test (below): build the tool through `AIFunctionFactory` and invoke it
with an object argument, asserting both success *and* the note. If that mechanism does not
hold, the fallback is a small wrapper value type (`LenientText` carrying `Value` +
`WasCoerced`) with a custom `JsonConverter` and a `JsonSchemaCreateOptions.TransformSchemaNode`
that pins the advertised schema back to `"string"`; the note then travels with the value and
needs no argument inspection. The fallback keeps every externally-visible behavior in this
spec identical — only the internal note-detection channel differs.

## Testing (TDD — RED first)

1. **Regression / end-to-end (write first):** create the `text_create` tool via the real
   `FileSystemToolFeature.GetTools` path and `InvokeAsync` it with `content` = a JSON object.
   Assert: no bind exception, backend received the object's JSON text, result has `ok: true`
   and the `note`. Repeat for `text_edit` with an object `oldString`. This proves the original
   bug is fixed at the binding layer that was failing.
2. **`TextArg.Coerce` unit tests:** one per `JsonValueKind` (string ⇒ no coercion; object,
   array, number, bool ⇒ coerced text; null ⇒ empty + coerced).
3. **Tool-body tests** with a fake registry/backend: object content ⇒ backend receives coerced
   text and result includes the note; string content ⇒ backend receives it unchanged and **no**
   note field is present.
4. **Cross-filesystem consistency:** the same object-content call against two different mounts
   yields the same coerced text and the same note (guards the chokepoint).
5. **Contract tests:** `FsCreateResult` / `FsEditResult` with `Note = null` serialize without a
   `note` key and pass `FsResultContract.TryValidate`; with a note they still validate.

## Blast radius

- **Changed:** `Domain/Tools/FileSystem/VfsTextCreateTool.cs`,
  `Domain/Tools/FileSystem/VfsTextEditTool.cs`,
  `Domain/Tools/FileSystem/FileSystemToolFeature.cs` (per-tool `AIFunctionFactoryOptions`),
  `Domain/DTOs/FileSystem/FsCreateResult.cs`, `Domain/DTOs/FileSystem/FsEditResult.cs`
  (optional `Note`), new `Domain/Tools/FileSystem/TextArg.cs` helper.
- **Untouched:** domain `TextEdit`, `IFileSystemBackend`, and all 7 backends;
  `filePath`/glob/search binding; every other VFS tool.
- **New tests** under `Tests/Unit/Domain/Tools/FileSystem/` and an integration test exercising
  the `AIFunctionFactory` binding path.
- No new packages (`AIFunctionFactoryOptions`, `ParameterBindingOptions`, and
  `AIFunctionArguments` are in `Microsoft.Extensions.AI.Abstractions` 10.6.0, already
  referenced by Domain). No config/env changes.
