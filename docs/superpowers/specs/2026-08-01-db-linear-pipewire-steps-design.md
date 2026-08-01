# dB-linear local volume steps on music satellites

**Date:** 2026-08-01
**Status:** Approved
**Follow-up to:** `2026-07-31-local-speaker-volume-design.md` (PR #62)

## Problem

Both local-volume backends take 10 commands to cross their full range, but the loudness
per command differs. The voice-only ALSA softvol has a dB-linear taper: every step is
5.1 dB. The PipeWire backend steps the sink's cubic display scale additively (`wpctl
set-volume -l 1.0 <sink> 10%+`), so a step is ~2.7 dB near the top and ~18 dB near the
bottom. The same spoken command feels different depending on which unit hears it.

## Goal

A volume step on a music satellite moves the same number of dB as on a voice-only
satellite: `--volume-step 10` (the default) gives ten equal steps of 5.1 dB across a
−51..0 dB range, on both unit types. Step-down bottoms out at −51 dB — quiet, never
silent. Mute stays the only way to silence a speaker.

## Approach

Chosen: do the dB math in the satellite (`satellite/src/volume.rs`), not in system
config. Rejected: `pactl` dB-relative steps (adds a pipewire-pulse dependency, splits
the master across two tools, still needs read-back for the floor) and reconfiguring
WirePlumber's volume curve (no stable supported knob, changes semantics for every
client, untestable provisioning-side logic).

## Behavior

On the PipeWire backend, `up`/`down` become read → compute → write:

1. Read the sink's current level with the existing query command
   (`wpctl get-volume <sink>`, the same output `seed` already parses).
2. Convert the cubic display value to dB: `dB = 60·log10(v)`.
3. Clamp into [−51, 0], snap to the nearest point on a grid anchored at 0 dB with
   spacing `51 × volume_step / 100` dB (default 10 → 5.1 dB).
4. Move one grid step up or down, clamp again.
5. Convert back (`v = 10^(dB/60)`) and write it as an absolute level:
   `wpctl set-volume <sink> <value>`.

Snapping makes repeated commands drift-free and heals an externally-moved sink on the
first spoken command. Clamps: step-up stops at 0 dB (100%); step-down stops at −51 dB.

Unchanged: mute/unmute (`wpctl set-mute`), seed, alert-hold/release, the whole ALSA
backend, the serialization gate, the wire protocol, the hub, provisioning, and all
flags. Stepping while muted changes the stored level without unmuting, matching the
ALSA side.

## Edge cases

- **Sink at true 0** (set externally): dB is −∞; the clamp brings it to the floor, so
  the first step-up lands one step above −51 dB.
- **`[MUTED]` in the read**: parse the number anyway; the step moves the stored level
  and the sink stays muted.
- **Failed or unparsable read**: return an error — the existing warn-and-no-cue path.
  Never guess a level.
- **`volume-step` not dividing 100**: the grid's top step is shorter, same as amixer's
  clamp on the raw range.

## Implementation shape

- A pure function in `volume.rs` mapping (current display value, direction, step) →
  new display value, holding all the math above. Output rounded to a fixed precision so
  command lines are stable for tests.
- `VolumeControl::step` on `Backend::Pipewire` uses `Backend::capture` for the read
  (the `Probe` test backend already supports a canned capture) and then runs the
  absolute `set-volume` command line. Both calls run inside the existing gate. The
  `-l 1.0` limit flag becomes unnecessary (the math clamps at 0 dB).
- `Backend::Alsa` keeps its one-line relative `amixer` command.

## Docs

- Rewrite the "`--volume-step` is not the same size on both" section of
  `satellite/CLAUDE.md`: both backends now take the same equal-dB steps over the same
  −51..0 dB range; note the read-modify-write shape and the snap-to-grid healing.
- Update the backend comment in `config.rs` accordingly.

## Testing

- Unit tests on the pure math: top clamp, floor clamp, snap from an off-grid value,
  silence input, a non-default step size.
- Probe-backend tests pinning the exact `wpctl set-volume` line produced from a known
  `wpctl get-volume` capture, up and down.
- A failed-read test asserting no write happens and an error surfaces.
- Existing suite (150 Rust tests) guards mute, hold, seed and the ALSA backend.
- TDD (Red-Green-Refactor) per project rules.

## On-hardware verification

One item joins the existing checklist: on a music unit, ten "baja el volumen local"
commands from 100% land at a quiet-but-audible level (−51 dB), and each step sounds
like the same-size change as on a voice-only unit.
