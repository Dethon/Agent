# HA Virtual Filesystem — Human-Readable Directory Names — Design Spec

## Goal

Make `glob` over the Home Assistant virtual filesystem **self-identifying**: the agent must be
able to pick the correct device from a `glob` listing alone, **without reading any file**. Today
entity directories are named by their technical id (`object_id` under `entities/`, full `entity_id`
under `areas/`), which for many integrations is cryptic (e.g. zigbee `climate.0x00158d0001abcd`).
Cryptic ids are ambiguous to the agent.

## Motivating failure

User: "turn off the AC in the living room." The agent ran `glob /ha/areas/living_room`, found **two**
`climate` entities with cryptic ids, could not tell which was the AC, and turned **both** off. The
distinguishing information (the friendly name) lived only inside each `state.yaml`, so `glob` was not
enough to choose correctly.

## Approach

Rename every entity directory segment to a **composite** that carries both the stable id and the
human name:

```
<id>_(<name-slug>)
```

- Under `entities/<class>/`, `<id>` is the bare `object_id`:
  `entities/climate/0x00158d00abcd_(aire-acondicionado-salon)/`
- Under `areas/<room>/`, `<id>` is the full `entity_id`:
  `areas/living_room/climate.0x00158d00abcd_(aire-acondicionado-salon)/`
- `<name-slug>` is the entity's `friendly_name`, slugified.
- If an entity has no `friendly_name`, the segment is the **bare `<id>`** (no `_(...)` suffix).
- **Area directories themselves are unchanged** — they keep the area slug (`areas/living_room/`),
  which is already readable; adding the area's display name is usually redundant.

Applies to **both** roots (`entities/` and `areas/`).

### Why composite (id + name) instead of name-only

- **Uniqueness for free.** The id guarantees a unique segment, so there is no need for collision
  suffixes or a slug→id lookup table.
- **Stable.** The id is the canonical, rename-proof key; the name is decorative.
- **Trivial, structural resolution.** The id is present in the path, so it can be recovered without
  consulting the catalog (see Resolution).
- **Backward compatible.** A bare-id path still resolves.

### Slug rules

`<name-slug>` = `friendly_name` → lowercase → Unicode-normalize and strip diacritics
(`ó`→`o`) → replace each run of non-`[a-z0-9]` characters with a single `-` → trim leading/trailing
`-` → cap length at 60 chars (trim back to the last whole `-`-delimited word when over). Result
charset is `[a-z0-9-]`.

The chosen format is **slugified** (not raw/readable) specifically because the slug charset cannot
contain `(`, `)`, `_(`, `/`, or spaces — so the name part can never collide with the template's
delimiter characters.

## Robustness to adversarial names

This is a hard requirement: a `friendly_name` containing the template's own symbols must not break
parsing.

1. **HA entity ids are restricted to `[a-z0-9_]`** (plus the `.` domain separator). They can **never**
   contain `(`. Therefore the **first** occurrence of `_(` in a segment is **always** the template
   delimiter — the id prefix has no `(` to create a false delimiter.
2. **Resolution keys on the id prefix and ignores the rest.** Everything after the first `_(` is
   decorative. A pathological suffix cannot corrupt id extraction.
3. **Slugifying the name eliminates the symbols at the source.** `Salón (1/2)` → `salon-1-2`. The
   generated name part never contains `_(`, `)`, `/`, or spaces.

These three together make the round-trip safe by construction.

## Resolution (read / info / exec)

`HaVfsPath.Parse` stays **pure and structural** (no catalog, no I/O). The only change: when the path
contains an entity-directory segment, strip the `_(...)` suffix to recover the id:

- `StripNice(segment)` = `segment` up to the first `_(`, or the whole `segment` if `_(` is absent.
- Under `entities/<class>/<seg>` → `object_id = StripNice(seg)`, `entity_id = "<class>.<object_id>"`.
- Under `areas/<room>/<seg>` → `entity_id = StripNice(seg)`.
- Leaf files (`state.yaml`, `<service>.sh`) hang under the entity-dir segment as before; the same
  strip applies to that segment.

Consequences:
- **Backward compatible:** `entities/light/kitchen/state.yaml` (no suffix) still resolves, as does
  the composite form. The agent may use either.
- **Drift-proof:** if a device is renamed mid-session (after the 5-min catalog cache refreshes), a
  previously-globbed composite path still resolves, because resolution only depends on the id prefix.
- Existence checks in `HaFileSystem` are unaffected — they operate on the recovered `entity_id`.

## Generation (glob / tree)

`HaTree.Directories` / `HaTree.Files` build the composite segments from the **cached catalog**, taking
`friendly_name` from each entity's attributes. `glob` therefore lists self-identifying directories in
both roots, so the agent distinguishes the two living-room ACs from the listing alone. Glob pattern
matching (`*`, `**`, `?`, `climate.*`) is unchanged and still matches against the (now composite)
segments.

## Components

- **`HaSlug`** (new) — single source of truth for the format:
  - `Slugify(string name)` → slug.
  - `Compose(string id, string? friendlyName)` → `"<id>_(<slug>)"` or bare `"<id>"` when the name is
    null/blank/slugs-to-empty.
  - `StripNice(string segment)` → id portion (substring before first `_(`, else whole).
  Most-tested unit (adversarial names).
- **`HaVfsPath`** — apply `HaSlug.StripNice` to the entity-dir segment in `ParseEntities`/`ParseAreas`
  (entity-dir, state-file, action-file cases). Remains pure/structural.
- **`HaTree`** — emit `HaSlug.Compose(...)` segments for entity directories (and their leaf-file
  parents) in both roots.
- **`HaCatalog`** — small helper to fetch an entity's `friendly_name` (from `Attributes`), used by
  `HaTree`.
- **`HaFileSystem`** — no logic change; exercised with composite inputs. (`state.yaml` rendering,
  the `.sh` model, search, and all JSON output shapes are untouched.)

No changes to: the MCP `fs_*` wrappers, the `filesystem://ha` resource, DI/wiring, or the slim index
prompt. Optional (decide during planning): one line in the workflow prompt noting directory names
read `<id>_(<name>)` and that either the composite or the bare id is accepted.

## Data flow

- **Discover:** `glob /ha/areas/living_room/*` → `HaTree` → composite segments
  (`climate.0x..._(aire-acondicionado-salon)`, `climate.0x..._(calefaccion-salon)`) → agent picks the
  AC from the names.
- **Act:** `exec path=/ha/areas/living_room/climate.0x..._(aire-acondicionado-salon)
  command="turn_off.sh"` → `HaVfsPath.Parse` strips the suffix → `entity_id = climate.0x...` → service
  call. (Passing the bare id works too.)

## Error handling

- Unchanged. Unknown/non-existent composite or bare path → existing not-found / `127` paths, keyed on
  the recovered id.
- A name that slugs to empty → bare-id segment (no suffix); resolution unaffected.

## Testing (TDD)

**Unit (`Tests/Unit/Domain/HomeAssistant/Vfs/`):**
- `HaSlug`: accents (`Salón`→`salon`), spaces, `/`, literal `_(` and `)` in the name, emoji, empty/blank,
  very long (length cap), duplicate names produce identical slugs (id still disambiguates).
- `HaSlug.StripNice` round-trip: ids containing underscores (`climate.ac_salon_(...)`→`climate.ac_salon`),
  names containing `_(`/`)`/`/`, bare id (no suffix), full `entity_id` under areas.
- `HaVfsPath`: parse composite entity-dir / state-file / action-file under both roots; bare-id paths
  still parse to the same node.
- `HaTree`: glob lists composite segments in `entities/` and `areas/`; pattern `climate.*` still matches.
- `HaFileSystem` read/info/exec: resolve from the composite **and** from the bare id; the two-AC
  scenario — two `climate` entities in one area are distinct directories distinguishable by name.

**Journey:** extend the existing journey test so discovery uses a composite path and acts on it.

## Out of scope

- Renaming the **area** directories to include the area display name (kept as the area slug).
- Annotating `glob` output shape (rejected — would break uniformity with the Sandbox/Vault backends).
- Listing per-entity names in the slim index prompt (rejected — prompt bloat; `glob` now suffices).
- Any change to `state.yaml`, the `.sh` action model, search semantics, or DI/wiring.

## Risks

- **Longer segments.** Composite names are longer (and mildly redundant for already-readable ids like
  `kitchen_(kitchen)`). Accepted: readability + disambiguation outweigh verbosity; the length cap
  bounds the worst case.
- **Name drift within a session.** Mitigated by the id-prefix resolution + bare-id fallback (a stale
  composite still resolves).
