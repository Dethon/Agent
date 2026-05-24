# Home Assistant Virtual Filesystem — Design Spec

## Goal

Expose Home Assistant as a first-class virtual filesystem mount (`filesystem://ha` → `/ha`)
so the agent navigates and controls it with the same `glob` / `read` / `search` / `exec`
verbs it already uses for Vault, Sandbox, and media. The bet is that a familiar, uniform,
self-documenting interface improves **discovery and accuracy**, and that folding HA into the
VFS family delivers **architectural consistency** — one verb set across every tool family,
zero new tool schemas for the model to learn.

This is explicitly **not** a token-reduction effort. A slim orientation index stays in the
prompt so the agent always knows the lay of the land; details move on-demand to the
filesystem ("index in context, details on disk").

Like every other VFS backend, this requires **zero agent-side changes** — `McpFileSystemDiscovery`
auto-mounts any server that publishes a `filesystem://` resource at session start.

## Constraints and choices

These were settled during brainstorming and drive the rest of the spec:

1. **Control = executable `.sh` files.** Services are modelled as `.sh` action files that the
   agent runs with `exec`. This makes `exec` idiomatic (running real files that exist, not an
   invented command syntax) and self-documenting via shell priors the model already holds.
2. **Dual-rooted namespace.** `entities/` (canonical, 1:1 with `entity_id`) plus `areas/`
   (a room-centric *view* built by template rendering, resolving to the same entities).
3. **GNU-flag arguments.** `--field value`; lists comma-separated; complex objects as a JSON
   value. `--help` advertises each field from the HA service schema.
4. **Slim index + full VFS.** Replace the per-entity prompt snapshot with a compact map;
   remove the typed `home_*` tools so all action flows through `exec`.
5. **Read + act only.** `fs_glob`, `fs_read`, `fs_info`, `fs_search`, `fs_exec` are
   implemented. `fs_create` / `fs_edit` / `fs_delete` are intentionally absent — you cannot
   author HA entities from here — and the registry returns the standard `unsupported_operation`
   envelope automatically.

## Architecture

A new filesystem backend lives inside the existing `McpServerHomeAssistant`, layered on the
existing `IHomeAssistantClient` (states, services, call-service, template render). The server
publishes a `filesystem://ha` resource; the agent's `McpFileSystemDiscovery` mounts it at `/ha`
through `VirtualFileSystemRegistry`. The backend implements the subset of `fs_*` MCP tools that
fit HA's shape; the agent's existing `FileSystemToolFeature` dispatches the 8 domain VFS tools
to it via `McpFileSystemBackend` with no modification.

```
Agent (FileSystemToolFeature, unchanged)
  → domain__filesystem__{glob_files,text_read,text_search,info,exec}
    → McpFileSystemBackend (unchanged)  →  MCP fs_* tools
      → McpServerHomeAssistant filesystem backend (NEW)
        → IHomeAssistantClient (states / services / call_service / render_template)
          → Home Assistant REST API
```

## Namespace layout

`entity_id` is `<class>.<object_id>` (e.g. `light.kitchen`). The tree is faithful to that:

```
/ha/
  entities/                          # canonical, 1:1 with entity_id
    light/
      kitchen/
        state.json                   # live state + attributes (read)
        turn_on.sh  turn_off.sh  toggle.sh   # applicable actions (exec)
    vacuum/
      roborock_s8/
        state.json
        start.sh  pause.sh  return_to_base.sh  clean_segment.sh
    sensor/
      salon_temp/
        state.json                   # read-only entity: no .sh files
  areas/                             # room view, template-rendered (a VIEW, not a copy)
    salon/
      light.salon/        → resolves to entities/light/salon/
      vacuum.roborock_s8/ → resolves to entities/vacuum/roborock_s8/
      sensor.salon_temp/  → resolves to entities/sensor/salon_temp/
    kitchen/
      light.kitchen/
      sensor.kitchen_temp/
    unassigned/                      # entities with no area
      sensor.outdoor/
```

- **`entities/<class>/<object_id>/`** — directory per entity. Dir name under `entities/` is the
  bare `object_id`; the parent dir is the class domain.
- **`areas/<area_slug>/<entity_id>/`** — built via `RenderTemplateAsync`
  (`{{ areas() }}`, `{{ area_entities(area_id) }}`), HA's only REST path into the registry.
  Entries are named by full `entity_id` for unambiguity and resolve to the same backend nodes
  as `entities/...`. Entities with no area land under `areas/unassigned/`.

### What `.sh` files an entity has

An entity's action files are the **class-domain services whose `target` accepts that entity** —
services in the entity's own class domain (the `entity_id` prefix) whose `target` matches it
(`{}` or no entity constraint = any; a `domain`-narrowed constraint must include the entity's
class). `null` target = not entity-targeted → not surfaced. Read-only entities (most `sensor`,
`binary_sensor`) match no services and therefore expose only `state.json`. Filenames are the bare
`<service>.sh`; collisions cannot occur because all action files for an entity come from one domain.

## Read surface

- `glob /ha/entities/light/*` → entity directories under the `light` domain.
- `glob /ha/entities/**` (and `/ha/areas/**`) → recursive listing.
- `read .../state.json` → current state, attributes, and `last_changed`, **always fetched fresh**
  from `GetStateAsync` at read time.
- `read .../turn_on.sh` (cat, no exec) → the same usage text `--help` produces. Idiomatic:
  cat to inspect, exec to run.
- `search` (`fs_search`) → grep over entity ids, friendly names, and rendered state (e.g. find
  every entity currently `on`). Backed by `ListStatesAsync` plus in-memory filtering.

### `state.json` rendering

One file per entity carrying state + attributes + `last_changed`, rendered as indented JSON via
`System.Text.Json` (no hand-rolled formatting), so arbitrary nested attributes are escaped
correctly by construction:

```json
{
  "entity_id": "light.kitchen",
  "state": "off",
  "last_changed": "2026-05-23T09:14:02Z",
  "attributes": {
    "brightness": null,
    "friendly_name": "Kitchen",
    "supported_color_modes": [
      "color_temp",
      "xy"
    ]
  }
}
```

## Action surface (the `.sh` model)

`exec` carries CWD = entity directory and a command naming an action file:

```
exec  path=/ha/entities/light/kitchen  command="turn_on.sh --brightness_pct 60"
  → { ok: true, changed: [ light.kitchen → on ] }     # exit 0
```

- **`<service>.sh --help`** → usage rendered from the HA service schema: each field with its
  `selector`-derived type, required flag, range/options, example, and the service description.

  ```
  turn_on.sh — call light.turn_on on light.kitchen
    --brightness_pct    INT     1-100
    --color_temp_kelvin INT     2000-6500
    --transition        FLOAT   seconds (optional)
  ```

- **GNU-flag arguments** parsed into HA `service_data`, typed via the field `selector`:
  - scalar → `--brightness_pct 60`
  - list (`selector.select.multiple`, or list-shaped) → `--segments salon,kitchen`
  - object → JSON value: `--advanced '{"eco":true}'`
- **Guard rail.** Any command that is not an existing `*.sh` in CWD (e.g. `cat`, `ls`,
  `rm -rf`, a hallucinated service) → exit `127`, with stderr listing the available action files
  in that directory. This leans into the bash prior so the agent self-corrects toward the real
  operations instead of inventing shell.
- **Service-call failure** (invalid argument, HA 400) → non-zero exit + stderr carrying HA's
  error and a "re-check the field selector and rebuild; don't retry the same shape" hint,
  preserving the recovery behaviour the current prompt already teaches.

## Components

All new code lives in `McpServerHomeAssistant`, behind `IHomeAssistantClient`, decomposed into
independently testable units:

1. **Path model** — resolves a virtual path to a typed node (`Root`, `EntitiesRoot`, `AreasRoot`,
   `ClassDir`, `AreaDir`, `EntityDir`, `StateFile`, `ActionFile`). Pure logic over in-memory
   snapshots of entities + services + the area map. No I/O.
2. **Action resolver** — given an entity and the service catalogue, computes its applicable
   `.sh` set via `target` matching.
3. **Help renderer** — `HaServiceDefinition` → usage text (also returned by `read` on a `.sh`).
4. **Argument parser** — GNU flags → `service_data` `JsonObject`, typed by each field's selector
   (scalar / list / object), with clear errors for unknown flags.
5. **State renderer** — `HaEntityState` → `state.json`.
6. **Area view** — `RenderTemplateAsync` → `area_slug → [entity_id]` map, cached per session.
7. **MCP `fs_*` tool wiring** — `fs_glob`, `fs_read`, `fs_info`, `fs_search`, `fs_exec` tools
   that drive units 1–6, plus the `filesystem://ha` resource and the prompt changes below.

**Caching:** the service catalogue (`ListServicesAsync`) and area map are read once and cached
for the session; entity **state is always read fresh** so `state.json` never goes stale.

## Data flow

- **Discover an entity:** `glob /ha/areas/salon/*` → area view → entity ids → resolve to nodes.
- **Inspect:** `read /ha/entities/light/kitchen/state.json` → `GetStateAsync` → JSON.
- **Learn an action:** `read .../turn_on.sh` or `exec ".../turn_on.sh --help"` → help renderer
  over the cached service schema.
- **Act:** `exec ".../turn_on.sh --brightness_pct 60"` → arg parser → `CallServiceAsync(light,
  turn_on, light.kitchen, {brightness_pct:60})` → exit 0 + `changed` summary.

## Prompt changes

- **Replace the full per-entity snapshot** (`HomeAssistantSetupSummary`) with a **slim index**:
  the mount root, the area list, class domains present, and entity counts. Enough to orient
  navigation without dumping every entity each turn.
- **Rewrite `HomeAssistantPrompt`** to teach the filesystem idiom: glob to find an entity →
  `read`/`cat` the `.sh` or `--help` for its schema → `exec` to act → read exit code (`0` =
  done; non-zero = re-inspect schema). Drop the `home_*`-tool workflow.

## What is removed

- The four typed tools `home_get_state`, `home_list_entities`, `home_list_services`,
  `home_call_service` (and their `McpServerHomeAssistant` MCP wrappers). All capability is now
  reachable through the VFS verbs. `IHomeAssistantClient` itself is retained — it becomes the
  backend's engine.

## Error handling

- **Missing entity / path** → not-found result from `fs_read` / `fs_info` (ENOENT-style).
- **Unsupported operation** (`create` / `edit` / `delete`) → standard `unsupported_operation`
  envelope, produced automatically by `McpFileSystemBackend` because the tool is absent.
- **Bad service arguments / HA 400** → non-zero `exec` exit + stderr with HA's message and a
  selector-rebuild hint.
- **Unknown command in `exec`** → exit `127` + available-actions listing.
- **HA unreachable / 401** → surfaced as an exec/read error with the underlying
  `HomeAssistantException` message.

## Testing

TDD throughout — RED test before each unit (per project rules). Tests run against a fake
`IHomeAssistantClient`.

**Unit** (`Tests/Unit/...`):
- Path model: resolution for every node type under both `entities/` and `areas/`.
- Action resolver: target matching (entity-targeted `{}`, domain-narrowed, non-targeted,
  read-only entity → no actions).
- Help renderer: field types from each selector shape; required vs optional; examples.
- Arg parser: scalars, comma lists, JSON objects, unknown flag → error.
- State renderer: state + attributes + `last_changed` → JSON.
- Area view: template output → area map; `unassigned` bucket.
- `fs_exec`: action-file → `CallServiceAsync` mapping; `--help` path; `127` guard; HA-error
  mapping to non-zero exit.

**Integration** (`Tests/Integration/...`):
- Discovery mounts `filesystem://ha` at `/ha`; end-to-end glob → read → exec through the real
  `FileSystemToolFeature` + `McpFileSystemBackend` against a stubbed HA.

## Phasing

1. **Backend skeleton + resource** — `filesystem://ha`, path model, `fs_info`/`fs_glob` over
   `entities/`; mounts and lists. (RED→GREEN per unit.)
2. **Read surface** — `state.json` rendering via `fs_read`; `fs_search`.
3. **Action surface** — `.sh` enumeration (action resolver), `--help`/`cat`, `fs_exec`
   service-call mapping, arg parser, `127` guard, error mapping.
4. **Areas view** — template-rendered `areas/` root resolving to the same nodes.
5. **Prompt cutover** — slim index, rewritten workflow prompt, remove `home_*` tools.

Auto-commit after each completed RED→GREEN→REVIEW triplet (project rule).

## Out of scope (for this spec)

- Filtering an entity's `.sh` set by `supported_features` (show only currently-capable args).
  Default is all class-domain services that target the entity; refine later if noisy.
- Vendor/integration-domain services (entity-targeted or not). v1 action files cover the entity's
  own class domain only — matching the existing prompt's guidance that primary actions live there.
  Surfacing vendor entity-targeted services (with disambiguated `<domain>.<service>.sh` filenames)
  is a later refinement.
- Write-style state setting (`fs_create`/`edit`) — deliberately unsupported.
- Real-time push / event subscription; reads are pull-only and always fresh.

## Risks

- **`exec` carries a bash prior.** The agent may attempt real shell. Mitigated by the `127`
  guard + available-actions listing and an explicit mount description ("`exec` runs HA service
  files, not a shell").
- **Round-trip cost vs the old snapshot.** Pull-based discovery adds turns. Mitigated by the
  slim index giving the agent its bearings up front.
- **Area registry only via templates.** `RenderTemplateAsync` is the sole REST path; if a
  template shape changes across HA versions the `areas/` view degrades. Mitigated by caching
  and falling back to `entities/` (canonical) which needs no templates.

## Style and layering rules to honour during implementation

- Backend logic lives in `McpServerHomeAssistant`; it depends only on `IHomeAssistantClient`
  (Domain contract) — no Infrastructure/Agent references from Domain-side tool logic.
- Modern C#: file-scoped namespaces, primary constructors, `record` DTOs, LINQ over loops,
  `CancellationToken` on all async paths, no XML doc comments.
- No trailing newline in non-test source files; follow existing `Fs*Tool` patterns in
  `McpServerSandbox/McpTools` for the MCP tool wrappers.
