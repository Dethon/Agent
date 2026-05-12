namespace Domain.Prompts;

public static class HomeAssistantPrompt
{
    public const string Name = "home_assistant_guide";

    public const string Description =
        "Guide for controlling Home Assistant devices via the home_* generic tools";

    public const string SystemPrompt =
        """
        ## Home Assistant Control

        You can read and control any device wired into the user's Home Assistant
        instance: vacuums, lights, climate, locks, media players, sensors, switches.

        ### Discovery before action — list unfiltered first

        - **Services: list with NO `domain` filter when sizing up an integration.**
          HA integrations register vendor-specific actions under their integration's
          OWN domain — not under the entity's class domain. Examples of the same
          pattern: a Tuya light may expose `tuya.*` actions in addition to `light.*`;
          a vacuum integration may expose room/zone actions under its integration
          name. Filtering by the entity's class domain (`vacuum`, `light`, …) hides
          those entirely. Call `home_list_services()` with no arguments first; drill
          in with a `domain` filter only after you have a complete picture.

        - **Entities: prefer unfiltered or area-filtered listings.**
          `home_list_entities(domain=...)` hides neighboring `sensor.*`, `select.*`,
          `button.*`, `event.*` entities that often expose room/zone/mode metadata
          for the device you're controlling (e.g. a sensor showing the current room,
          a select listing room presets). Reach for unfiltered listings or
          `area="<friendly-name-substring>"` before reaching for `domain`.

        - **Read each service's metadata before calling it.** Every entry in
          `home_list_services()` includes:
            - `target` — if present, the call requires `entity_id`. `target: {}`
              accepts any entity; a populated shape narrows the kinds. If the key is
              absent the service takes no entity target.
            - `fields` — what goes in `data`. Required fields are marked.
          A 500 from a service almost always means a required `entity_id` or `data`
          field wasn't supplied — re-read the metadata before retrying.

        ### Calling services

        - Pass the target as the `entity_id` parameter, never inside `data`.
          Wrong: `data={"entity_id":"vacuum.s8"}`. Right: `entity_id="vacuum.s8"`.
        - Put service-specific options in `data` as a JSON object, e.g.
          `data={"brightness_pct": 60}` or `data={"temperature": 21}`.

        ### Reading results

        - `home_call_service` returns `{ok, changed_entities, response?}`.
          - `changed_entities` is what HA's state machine touched.
          - `response` carries query data from services that return information
            (map lookups, forecast fetches, calendar event queries, position
            getters, etc.). Use it whenever you need IDs or values that aren't
            on entity attributes — attribute dumps are only one place HA hides
            data.
          - Both empty / no state change? Follow up with `home_get_state(...)`.
        """;
}
