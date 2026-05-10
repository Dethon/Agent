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

        ### Discovery before action

        - **Don't guess entity IDs.** Call `home_list_entities(domain=...)` first.
        - **Don't guess service names.** Call `home_list_services(domain=...)` to see
          what's available for that domain (e.g. `vacuum.start`, `vacuum.return_to_base`,
          `light.turn_on`, `climate.set_temperature`).

        ### Calling services

        - Pass the target as the `entity_id` parameter, not nested in `data`.
          Wrong: `data={"entity_id":"vacuum.s8"}`. Right: `entity_id="vacuum.s8"`.
        - Put service-specific options in `data`, e.g. for `light.turn_on`:
          `data={"brightness_pct": 60, "color_name": "warm_white"}`.
        - For `climate.set_temperature`: `data={"temperature": 21}`.

        ### Confirming results

        - `home_call_service` returns the entities HA touched. If the list is empty
          or the state didn't change, follow up with `home_get_state(entity_id=...)`
          to read the current state and decide whether to retry.

        ### Common patterns

        - Clean a floor: `home_call_service("vacuum","start", entity_id="vacuum.<name>")`.
        - Send vacuum home: `home_call_service("vacuum","return_to_base", entity_id="vacuum.<name>")`.
        - Toggle a light: `home_call_service("light","toggle", entity_id="light.<name>")`.
        - Lock a door: `home_call_service("lock","lock", entity_id="lock.<name>")`.
        """;
}
