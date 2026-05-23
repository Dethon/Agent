# HA Virtual Filesystem — Single Canonical Path — Design Spec

## Goal

Make entity-path resolution **strict and consistent**: exactly **one** valid directory name per
entity — the composite name a listing shows — accepted identically by every tool. Remove the dual
"bare id **or** composite, both resolve" leniency that today applies to `read`/`exec`/`info`/`search`
but **not** to `glob`.

## Motivating failure

The agent had the correct directory names from a `glob` (call #1), then **reconstructed a path** for
call #2 instead of copying what it had just been given. This slips through today because the
resolution layer is lenient and inconsistent:

- `read`/`exec`/`info`/`search` run the entity segment through `HaSlug.StripNice`, which keeps only
  the substring before the first `_(` and **discards everything after it**. So a bare id resolves,
  and even a *wrong* `_(...)` suffix resolves (the suffix is ignored entirely).
- `glob` does the opposite: it matches the **exact** composite name (`HaSlug.Compose(id, friendly)`)
  against its pool, so a bare id matches **nothing**.

The two mechanisms disagree. An agent that trusts "the bare id always works" gets silent success on
`exec`/`read` but a silent empty result on `glob`, and reconstructed paths are never validated against
the real directory name. That inconsistency is the trap this change closes.

## Decision (supersedes the friendly-names spec)

The friendly-names spec (`2026-05-23-ha-vfs-friendly-names-design.md`) deliberately made bare-id
resolution a **feature** ("Backward compatible: a bare-id path still resolves", "The agent may use
either", drift-proofing in §Risks). **This spec reverses that specific decision.** Everything else in
the friendly-names design — the `<id>_(<slug>)` format, the slug rules, adversarial-name robustness,
glob generation — is kept unchanged.

New rule: there is exactly **one** canonical directory name per entity, and only it resolves.

- Canonical name = `HaSlug.Compose(objectOrEntityId, friendlyName)` = `id_(slug)` when the entity has
  a friendly name, and **plain `id`** when it does not (a suffix cannot be invented).
- For an entity **with** a friendly name, the bare id **no longer** resolves.
- A wrong or extra `_(...)` suffix **no longer** resolves.
- `glob` is already strict and is **untouched**; `read`/`exec`/`info`/`search` become strict the same
  way, so all tools agree by construction (they all compose names through the same `HaSlug.Compose`).
- On a **near-miss** — the object-id is recognizable but the full segment isn't canonical — resolution
  returns not-found **with a hint** naming the correct directory (e.g. "Did you mean
  `fran_s_office_(hvac)`?"). An unrecognizable object-id returns a plain not-found.

## Approach (chosen)

**Centralized strict resolver.** `HaVfsPath.Parse` stops being the authority on entity identity;
identity resolution moves to a single catalog-aware helper that every path-taking operation calls.
Considered and rejected for now: a full "canonical name → entity index" unification shared with
`HaTree` — more churn (changes `Parse`'s contract *and* `HaTree`) for the same observable behavior;
recorded as a possible later cleanup.

## Resolution

- **`HaVfsPath.Parse`** becomes purely **structural**: it returns the tree (`entities`/`areas`), the
  class or area, the **raw** entity segment, and the leaf kind (state file / action file). It no
  longer runs `StripNice` to assert an `EntityId`.
- **`HaFileSystem.ResolveEntity(catalog, node)`** (new, catalog-aware):
  1. recover a candidate object/entity id from the raw segment via `HaSlug.StripNice`,
  2. look the entity up in the catalog,
  3. accept **only if** `HaSlug.Compose(candidateId, entity.friendlyName) == rawSegment`.
  - Returns the resolved entity, or a not-found that carries a hint (the correct canonical name) when
    step 2 found an entity but step 3 failed.
- All path-taking consumers route through it: `Resolve` (info), `ReadStateAsync`/`ReadActionAsync`,
  `InfoAsync`, `ScopeEntities` (search), and the entity-dir existence check in `ExecAsync`.

## Generation (glob / tree)

Unchanged. `HaTree.Directories`/`HaTree.Files` still emit `HaSlug.Compose(...)` segments, and `glob`
still matches against them. Because resolution now composes names the same way, a path copied verbatim
from a `glob` result always resolves, and nothing else does.

## Components

- **`HaVfsPath`** — node carries `EntitySegment` (raw) plus structural fields; no `StripNice`
  authority. Pure/structural, no catalog.
- **`HaFileSystem`** — new `ResolveEntity` helper (strict match + hint); `Resolve`,
  `ReadStateAsync`, `ReadActionAsync`, `InfoAsync`, `ScopeEntities`, and `ExecAsync`'s existence
  check switch to it.
- **`HaSlug`** — unchanged; `StripNice` now used only inside `ResolveEntity` for candidate extraction
  and hint generation.
- **`HomeAssistantPrompt`** — delete the "address by the full segment **or by the bare id**; both
  resolve" line; state the single rule: the path is the exact directory name a listing/`glob`
  returns — copy it verbatim, do not reconstruct it from an id.

No changes to: the MCP `fs_*` wrappers, the `filesystem://ha` resource, DI/wiring, the slim index
prompt, `state.json` rendering, the `.sh` action model, search semantics, or any other backend / the
generic VFS tools.

## Data flow

- **Discover:** `glob /ha/areas/salon/*` → composite segments → agent copies the exact one it wants.
- **Act:** `exec path=/ha/areas/salon/climate.0x..._(aire-acondicionado-salon) command="turn_off.sh"`
  → `Parse` (structural) → `ResolveEntity` confirms the segment is canonical → service call.
- **Near-miss:** `exec path=/ha/entities/climate/fran_s_office command="turn_off.sh"` (bare id, but a
  friendly name exists) → `ResolveEntity` recognizes `fran_s_office`, finds the canonical name differs
  → not-found (`127`) with hint `fran_s_office_(hvac)` → agent retries with the correct name.

## Error handling

- Bare id when a friendly name exists, or a wrong/extra suffix → not-found (`read`/`info`) or `exit
  127` (`exec`) **with a hint** carrying the correct canonical directory name.
- Unrecognizable object-id → plain not-found, no hint.
- Entity with no friendly name → resolves **only** as the bare `id`; a spurious `_(...)` suffix on it
  is a near-miss → not-found + hint (`id`).

## Testing (TDD)

**Unit (`Tests/Unit/Domain/HomeAssistant/Vfs/`):**

- `HaVfsPath`: parses entity-dir / state-file / action-file under both roots to the **raw** segment
  + structural fields; no id reconstruction.
- `HaFileSystem` read / info / exec / search-scope:
  - exact composite name → resolves;
  - bare id when a friendly name exists → not-found **+ hint** with the canonical name;
  - wrong/extra suffix → not-found **+ hint**;
  - entity with no friendly name → resolves as bare `id`;
  - bare `id` with a spurious suffix on a no-friendly-name entity → not-found + hint.
- Update/replace existing tests that assert bare-id or arbitrary-suffix resolution (these encoded the
  now-removed leniency).

**Journey:** discovery `glob`s, then acts on the **exact** returned path; add a near-miss step
asserting the hint.

## Out of scope

- Other filesystem backends and the generic VFS tool descriptions.
- The `<id>_(<slug>)` format itself, slug rules, area directory names, `state.json`, the `.sh` model,
  search semantics, DI/wiring.
- The "canonical name → entity index" unification (possible later cleanup, not now).

## Risks

- **Mid-session rename drift.** A path globbed *before* a device is renamed in HA will stop resolving
  once the 5-minute catalog cache refreshes (its canonical name changed). The old lenient design
  resolved it silently. Under this design it fails **loudly with a hint** pointing at the new name, so
  the agent re-globs and self-corrects. Net: better for correctness than silent leniency. Accepted.
- **More verbose flow.** The agent must list before acting and carry the exact name; it can no longer
  shortcut with the bare id. This is the intended discipline ("copy what a call returned verbatim")
  and the direct fix for the motivating guess-the-path failure. Accepted.

## Status

Implemented 2026-05-23 — see plan `docs/superpowers/plans/2026-05-23-ha-vfs-canonical-path.md`.
