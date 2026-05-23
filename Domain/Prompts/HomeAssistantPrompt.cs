namespace Domain.Prompts;

public static class HomeAssistantPrompt
{
    public const string Name = "home_assistant_guide";

    public const string Description =
        "Guide for controlling Home Assistant devices via the /ha virtual filesystem";

    public const string SystemPrompt =
        """
        ## Home Assistant Control (`/ha` filesystem)

        Home Assistant is mounted at `/ha` and used through the standard filesystem
        tools. The "## Current Home Assistant setup" index appended below lists the
        rooms, device classes, and counts — consult it first to orient.

        ### Layout

        - `/ha/entities/<class>/<id>/` — one directory per entity (e.g.
          `/ha/entities/light/kitchen/`). Contains `state.yaml` (live state +
          attributes) and one `<service>.sh` per available action.
        - `/ha/areas/<room>/<entity_id>/` — the same entities grouped by room.
        - Directory names read `<id>_(<friendly-name>)` (e.g.
          `climate.0x00158d00abcd_(aire-acondicionado-salon)`) so `glob` alone identifies a
          device — pick by the name. You may address a path by the full segment or by the bare
          `<id>`; both resolve.

        ### Workflow

        1. Find the entity: `glob_files` under `/ha/entities/<class>` or
           `/ha/areas/<room>`, or read the setup index.
        2. Inspect when you need an attribute as input: `text_read`
           `/ha/.../state.yaml`.
        3. Learn an action's arguments: `exec` `<service>.sh --help` (or
           `text_read` the `.sh` file — same content).
        4. Act: `exec` from the entity directory, e.g.
           `exec(path="/ha/entities/light/kitchen", command="turn_on.sh --brightness_pct 60")`.

        ### Reading results

        - `exitCode` 0 = the action succeeded (`stdout` carries `{ok, changed[]}` and
          any service `response`). It is authoritative — do NOT read `state.yaml`
          afterwards to confirm; HA propagates state asynchronously and the read is stale.
        - `exitCode` 2 = bad argument: re-run `--help` and rebuild; don't repeat the
          same shape.
        - `exitCode` 1 = HA rejected the call; `stderr` has the reason.
        - `exitCode` 127 = not a real action file. `/ha` is NOT a shell — only the
          listed `*.sh` files run. `stderr` lists the available actions.

        ### Notes

        - `state.yaml` reads are always live. Read one only when you need a specific
          attribute as INPUT to the next action (e.g. `source_list` before
          `select_source`).
        - Room targets: use the area `id` slug from the index, not the display name.
        - Climate: read the ambient (a room temperature sensor, or the climate entity's
          `current_temperature`) before choosing a direction; set heating targets above
          ambient and cooling targets below; change mode first if it conflicts.
        """;
}