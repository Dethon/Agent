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
          `/ha/entities/light/kitchen/`). Contains `state.json` (live state +
          attributes) and one `<service>.sh` per available action.
        - `/ha/areas/<room>/<entity_id>/` — the same entities grouped by room; `<room>` is
          the area `id` slug (e.g. `salon`), the same value shown in parentheses beside each
          room in the setup index — not the display name.
        - Each entity directory's name carries its friendly name as `..._(<friendly-name>)`
          (e.g. `0x00158d00abcd_(aire-acondicionado-salon)` under `entities/climate/`, or the
          full `climate.0x00158d00abcd_(aire-acondicionado-salon)` under `areas/<room>/`) so
          `glob_files` alone identifies a device — pick by the name. Use that exact directory
          name verbatim in later calls; a bare id or a guessed `_(...)` suffix will NOT resolve
          (a near-miss returns a "did you mean" hint with the correct name).

        ### Workflow

        1. Find the entity: `glob_files` under `/ha/entities/<class>` or
           `/ha/areas/<room>`, or read the setup index. To list an entity's available
           actions, `glob_files` `<entity-dir>/*.sh`.
        2. Inspect when you need an attribute as input: `text_read`
           `/ha/.../state.json`.
        3. Learn an action's arguments: `exec` `<service>.sh --help`. The `.sh` files are
           action stubs, not scripts — don't `text_read` them; `--help` prints the field list.
        4. Act: `exec` from the entity directory, e.g.
           `exec(path="/ha/entities/light/kitchen", command="turn_on.sh --brightness_pct 60")`.

        ### Reading results

        - `exitCode` 0 = the action succeeded (`stdout` carries `{ok, changed[]}` and
          any service `response`). This is your confirmation — do NOT read `state.json`
          afterwards to check it worked. HA performs the action right away but only
          writes the new value into its state store after a short delay, so a read
          taken now still returns the OLD value and would wrongly look like nothing
          changed. Trust the `exitCode` and `changed[]`; never re-read to verify.
        - `exitCode` 2 = bad argument: re-run `--help` and rebuild; don't repeat the
          same shape.
        - `exitCode` 1 = HA rejected the call; `stderr` has the reason.
        - `exitCode` 127 = not a real action file. `/ha` is NOT a shell — only the
          listed `*.sh` files run. `stderr` lists the available actions.

        ### Notes

        - `state.json` always reflects HA's current stored state (nothing is cached
          on our side), but that store lags an action you just issued by the delay
          noted above. So read it only to fetch an attribute you did NOT just change,
          as INPUT to the next action (e.g. `source_list` before `select_source`) —
          never to confirm a change you just made.
        - Area/room ids: HA generates an area's `id` once, as a lowercase slug of its name at
          creation (`Salón` → `salon`), and keeps it fixed even if the area is later renamed.
          So the id is NOT something you can reliably derive yourself from the display name —
          accents, spaces, and past renames make a guess wrong. Read the real value, which
          appears verbatim in two places: the parentheses beside each room in the setup index,
          and the `<room>` segment under `/ha/areas/` (`glob_files /ha/areas/*` lists them).
          Whenever an action argument names a room or area, pass that slug, never the display
          name (e.g. a vacuum's `--cleaning_area_id salon`). In `--help`, such arguments are
          typed `AREA_ID (slug)`.
        - Climate: read the ambient (a room temperature sensor, or the climate entity's
          `current_temperature`) before choosing a direction; set heating targets above
          ambient and cooling targets below; change mode first if it conflicts.
        """;
}