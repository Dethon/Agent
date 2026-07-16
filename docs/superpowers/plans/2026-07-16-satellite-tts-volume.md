# Satellite TTS/Cue Volume Calibration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the agent's voice (TTS + cue beeps) its own calibrated volume on music satellites, independent of music, so the MiniAmp unit (no hardware volume) stops being too loud.

**Architecture:** A second ALSA softvol PCM (`pcm.tts`, control `TTS` on the speaker card) mirroring the existing `pcm.music` pattern; the satellite's `--snd-command` plays through it instead of raw `-D pipewire`. Provisioning materializes the control and sets `TTS_VOLUME` (default 60%) on every run. Spec: `docs/superpowers/specs/2026-07-16-satellite-tts-volume-design.md`.

**Tech Stack:** bash (`scripts/provision-satellite-rs.sh`), ALSA softvol, PipeWire, systemd drop-ins. No Rust, no .NET.

## Global Constraints

- Softvol block verbatim: `control { name "TTS" card ${outctl} }`, `min_dB -51.0`, `max_dB 0.0`, `resolution 256` (identical to `pcm.music`).
- Env knob: `TTS_VOLUME`, default `60` (percent). Re-asserted on every provisioning run (authoritative re-provision, same as the existing sink `0.8`).
- Only the music/PipeWire provisioning branch changes. The Jabra voice-only path is untouched.
- No repo test harness exists for bash provisioning scripts: verification is `bash -n` (outer script AND extracted remote heredoc body) plus on-device validation (Task 4).
- The pre-commit hook re-stages whole files — the working tree must match each intended commit exactly (no partial staging). `git add` explicit paths only.

---

### Task 1: Commit the pending fran-office provisioning work (prerequisite)

The working tree already carries the validated-on-device XVF3800/micclock + MiniAmp-detection work (unit went live 2026-07-16). Task 2 edits the same script, and the pre-commit hook re-stages whole files, so this must be its own commit first.

**Files:**
- Modify (already modified, commit as-is): `McpChannelVoice/appsettings.json`, `satellite/CLAUDE.md`, `satellite/deploy/nabu-satellite.service`, `scripts/provision-satellite-rs.sh`
- Add (untracked): `satellite/deploy/nabu-micclock.service`

**Interfaces:**
- Produces: a clean working tree; `scripts/provision-satellite-rs.sh` at the state Task 2's edits anchor to (micclock probe, `outcard`/`outctl` detection, `pcm.music`-only asound.conf).

- [ ] **Step 1: Confirm the tree contains only this work**

Run: `git status --porcelain`
Expected: exactly the five paths above (` M` ×4, `??` ×1). If anything else appears, stop and ask the operator.

- [ ] **Step 2: Commit (explicit paths)**

```bash
git add McpChannelVoice/appsettings.json satellite/CLAUDE.md \
        satellite/deploy/nabu-satellite.service satellite/deploy/nabu-micclock.service \
        scripts/provision-satellite-rs.sh
git commit -m "feat(satellite): XVF3800 micclock provisioning + I2S DAC HAT output detection

Validated on-device (fran-office, 2026-07-16): voice + music + duck.

Claude-Session: https://claude.ai/code/session_01ApDq2P4YRNQBJzfRU4sRUr"
```

- [ ] **Step 3: Verify clean tree**

Run: `git status --porcelain`
Expected: empty output.

---

### Task 2: `pcm.tts` softvol + `TTS_VOLUME` plumbing in the provisioning script

**Files:**
- Modify: `scripts/provision-satellite-rs.sh` (five edits; line numbers refer to the post-Task-1 file)

**Interfaces:**
- Consumes: `outctl` (speaker card control name, already computed), `uid`, the `ASOUND` heredoc, the `DROPEOF` drop-in heredoc.
- Produces: env knob `TTS_VOLUME` (percent, default 60); ALSA PCM `tts` and mixer control `TTS` on card `${outctl}`; drop-in `--snd-command "aplay -D tts …"`. Task 3 documents these names; Task 4 exercises them.

- [ ] **Step 1: Usage header — advertise the knob**

Edit lines 5–6 (`old_string` → `new_string`):

```bash
#   Music satellite: MUSIC_HUB=<snapserver-host> MUSIC_ROOM=<player-name> \
#                    scripts/provision-satellite-rs.sh <user@host>
```

→

```bash
#   Music satellite: MUSIC_HUB=<snapserver-host> MUSIC_ROOM=<player-name> [TTS_VOLUME=<pct>] \
#                    scripts/provision-satellite-rs.sh <user@host>
```

Then append to the header paragraph, directly after the line `# present, else the 3.5mm jack. The mic card stays OUT of PipeWire, owned raw by the satellite.`:

```bash
# On music units the satellite's own playback (TTS + cues) additionally flows through a `tts`
# ALSA softvol (control "TTS" on the speaker card, set to TTS_VOLUME%, default 60) so the agent
# voice is calibrated independently of music — the volume knob for amp HATs that have none
# (e.g. the MiniAmp). Tune live: amixer -c <card> sset TTS <pct>% ; persist: sudo alsactl store.
```

- [ ] **Step 2: Pass `TTS_VOLUME` into the remote heredoc**

Edit the comment + ssh line (currently lines 54–56):

```bash
# Quoted heredoc + MIC/MUSIC_HUB/MUSIC_ROOM env vars: nothing is expanded locally; the remote
# bash evaluates everything (and reads vars from the command-prefix assignments).
ssh "${SSHOPTS[@]}" "$host" MIC="${mic}" MUSIC_HUB="${MUSIC_HUB:-}" MUSIC_ROOM="${MUSIC_ROOM:-}" bash -se <<'EOF'
```

→

```bash
# Quoted heredoc + MIC/MUSIC_HUB/MUSIC_ROOM/TTS_VOLUME env vars: nothing is expanded locally; the
# remote bash evaluates everything (and reads vars from the command-prefix assignments).
ssh "${SSHOPTS[@]}" "$host" MIC="${mic}" MUSIC_HUB="${MUSIC_HUB:-}" MUSIC_ROOM="${MUSIC_ROOM:-}" TTS_VOLUME="${TTS_VOLUME:-60}" bash -se <<'EOF'
```

- [ ] **Step 3: Add `pcm.tts` to the asound.conf heredoc**

Replace the `music` PCM comment + heredoc (currently lines 201–215):

```bash
    # `music` PCM: snapclient -> a softvol the satellite ducks (amixer -c <card> sset Music <pct>%)
    # -> PipeWire -> speaker. The softvol CONTROL is stored on the speaker card (HAT when present,
    # else the jack); the audio itself flows through PipeWire either way. NO pcm.!default
    # (PipeWire's own default stands); capture stays direct plughw.
    outctl="${outcard:-Headphones}"
    sudo tee /etc/asound.conf >/dev/null <<ASOUND
pcm.music {
    type softvol
    slave.pcm "pipewire"
    control { name "Music" card ${outctl} }
    min_dB -51.0
    max_dB 0.0
    resolution 256
}
ASOUND
```

→

```bash
    # `music` PCM: snapclient -> a softvol the satellite ducks (amixer -c <card> sset Music <pct>%)
    # -> PipeWire -> speaker. `tts` PCM: the satellite's own playback (TTS + cues) -> a softvol
    # holding the calibrated agent-voice level (TTS_VOLUME) -> PipeWire -> speaker, independent
    # of music — the volume knob for amp HATs that have none (e.g. the MiniAmp). Both CONTROLs
    # are stored on the speaker card (HAT when present, else the jack); the audio itself flows
    # through PipeWire either way. NO pcm.!default (PipeWire's own default stands); capture
    # stays direct plughw.
    outctl="${outcard:-Headphones}"
    sudo tee /etc/asound.conf >/dev/null <<ASOUND
pcm.music {
    type softvol
    slave.pcm "pipewire"
    control { name "Music" card ${outctl} }
    min_dB -51.0
    max_dB 0.0
    resolution 256
}

pcm.tts {
    type softvol
    slave.pcm "pipewire"
    control { name "TTS" card ${outctl} }
    min_dB -51.0
    max_dB 0.0
    resolution 256
}
ASOUND
```

- [ ] **Step 4: Materialize the control and set the level**

Directly after the `wpctl set-volume @DEFAULT_AUDIO_SINK@ 0.8` line, insert:

```bash

    # Calibrate the agent voice: a softvol CONTROL only materializes on first open of its PCM,
    # so play 1 s of silence through pcm.tts, then set the level (re-asserted on every provision,
    # like the 0.8 sink volume above) and store ALSA state so it survives a power cut, not just
    # a clean shutdown. Runs as the login user: on Raspberry Pi OS the default user is in the
    # `audio` group at login (needed to create the control); a first-ever provision under a user
    # only just added to `audio` above would fail here — re-login (rerun the script) fixes it.
    XDG_RUNTIME_DIR=/run/user/$uid timeout 10 aplay -D tts -r 22050 -c 1 -f S16_LE -t raw -d 1 /dev/zero
    amixer -c "${outctl}" sset TTS "${TTS_VOLUME}%"
    sudo alsactl store
```

- [ ] **Step 5: Route the satellite through `pcm.tts` in the drop-in**

Edit the drop-in comment's first two lines (currently lines 230–231):

```bash
    # nabu-satellite drop-in: TTS/cues -> PipeWire (speaker); duck the `Music` softvol while
    # active; XDG so aplay -D pipewire connects. NO --keep-warm: PipeWire's exact drain re-arms
```

→

```bash
    # nabu-satellite drop-in: TTS/cues -> the `tts` softvol (calibrated agent-voice level) ->
    # PipeWire (speaker); duck the `Music` softvol while active; XDG so aplay reaches PipeWire.
    # NO --keep-warm: PipeWire's exact drain re-arms
```

And in the `DROPEOF` heredoc ExecStart:

```bash
  --snd-command "aplay -D pipewire -r 22050 -c 1 -f S16_LE -t raw" \\
```

→

```bash
  --snd-command "aplay -D tts -r 22050 -c 1 -f S16_LE -t raw" \\
```

- [ ] **Step 6: Syntax-check outer script AND remote heredoc body**

`bash -n` does not parse quoted-heredoc content, so check the body separately:

```bash
bash -n scripts/provision-satellite-rs.sh
sed -n "/bash -se <<'EOF'/,/^EOF$/p" scripts/provision-satellite-rs.sh | sed '1d;$d' \
  > /tmp/claude-1000/-home-dethon-repos-agent/63a7f10d-eccf-45ea-b7b2-a7a52de2a538/scratchpad/remote-body.sh
bash -n /tmp/claude-1000/-home-dethon-repos-agent/63a7f10d-eccf-45ea-b7b2-a7a52de2a538/scratchpad/remote-body.sh
```

Expected: no output from either `bash -n` (exit 0).

- [ ] **Step 7: Commit**

```bash
git add scripts/provision-satellite-rs.sh
git commit -m "feat(satellite): TTS/cue softvol volume on music units (TTS_VOLUME, default 60%)

Claude-Session: https://claude.ai/code/session_01ApDq2P4YRNQBJzfRU4sRUr"
```

---

### Task 3: Documentation (service unit comment + satellite/CLAUDE.md)

**Files:**
- Modify: `satellite/deploy/nabu-satellite.service` (comment block only)
- Modify: `satellite/CLAUDE.md` (Build & Deploy section)

**Interfaces:**
- Consumes: names from Task 2 — PCM `tts`, control `TTS`, env `TTS_VOLUME` (default 60).
- Produces: nothing executable; docs only.

- [ ] **Step 1: Update the music-satellites paragraph in the unit comment**

In `satellite/deploy/nabu-satellite.service`, edit (currently lines 27–28):

```
# a systemd drop-in (nabu-satellite.service.d/pipewire.conf) that OVERRIDES the ExecStart below:
# --snd-command targets PipeWire (aplay -D pipewire -> jack), --wake-snd-command keeps the
```

→

```
# a systemd drop-in (nabu-satellite.service.d/pipewire.conf) that OVERRIDES the ExecStart below:
# --snd-command targets PipeWire through the `tts` ALSA softvol (aplay -D tts -> speaker), which
# holds the calibrated agent-voice level (provision-time TTS_VOLUME, default 60%) independent of
# music — the volume knob for amp HATs that have none (MiniAmp); --wake-snd-command keeps the
```

- [ ] **Step 2: Add the knob to `satellite/CLAUDE.md`**

In the **Build & Deploy** section, after the sentence ending `plus a USB-autosuspend-off udev rule keyed on the device's vendor:product.`, insert:

```
Music units route the satellite's own playback (TTS + cues) through a `tts` ALSA softvol (control `TTS` on the speaker card, set to `TTS_VOLUME`%, default 60, re-asserted per provision) so agent-voice loudness is calibrated independently of music — the volume knob for amp HATs that have none (MiniAmp); tune live with `amixer -c <card> sset TTS <pct>%` + `sudo alsactl store`.
```

- [ ] **Step 3: Commit**

```bash
git add satellite/deploy/nabu-satellite.service satellite/CLAUDE.md
git commit -m "docs(satellite): document the tts softvol / TTS_VOLUME knob

Claude-Session: https://claude.ai/code/session_01ApDq2P4YRNQBJzfRU4sRUr"
```

---

### Task 4: Deploy to fran-office + on-device verification + bake calibrated default

Interactive task — needs the operator (SSH to the Pi at `192.168.5.11`, listening by ear). Do NOT mark complete on partial signals.

**Files:**
- Modify (possibly): `scripts/provision-satellite-rs.sh` (only the `TTS_VOLUME:-60` default, if calibration lands elsewhere)

**Interfaces:**
- Consumes: everything from Task 2. The provisioning invocation needs `MUSIC_HUB`/`MUSIC_ROOM` — recover them from the Pi rather than guessing.

- [ ] **Step 1: Confirm SSH target with the operator**

Ask the operator for the SSH user (`<user>@192.168.5.11`; there is no ~/.ssh/config alias). Export for the steps below: `SAT=<user>@192.168.5.11`

- [ ] **Step 2: Recover the music parameters from the Pi**

```bash
ssh $SAT 'grep ExecStart /etc/systemd/system/snapclient.service'
```

Expected output shape: `ExecStart=/usr/bin/snapclient --host <MUSIC_HUB> --port 1704 --hostID <MUSIC_ROOM> --player alsa --soundcard music`. Note both values.

- [ ] **Step 3: Re-provision**

```bash
MUSIC_HUB=<from step 2> MUSIC_ROOM=<from step 2> scripts/provision-satellite-rs.sh $SAT
```

Expected: builds, copies, ends with `Provisioned. Logs: journalctl -u nabu-satellite -f`. Watch for the new materialize/set lines succeeding (no `Unable to find simple control 'TTS'`).

- [ ] **Step 4: Verify plumbing on the Pi**

```bash
ssh $SAT 'grep -A7 pcm.tts /etc/asound.conf; grep snd-command /etc/systemd/system/nabu-satellite.service.d/pipewire.conf; amixer -c sndrpihifiberry sget TTS; systemctl is-active nabu-satellite nabu-micclock snapclient'
```

Expected: the `pcm.tts` block; `--snd-command "aplay -D tts …"`; `TTS` at `60%`; all three services `active`.

- [ ] **Step 5: Functional verification (by ear)**

1. Say "ok nabu" + a question → cue beep and reply audibly quieter than before.
2. Play music → level unchanged from before this change.
3. Speak to the agent while music plays → music ducks and restores.

- [ ] **Step 6: Calibrate**

Operator adjusts until right (`ssh $SAT 'amixer -c sndrpihifiberry sset TTS 50%'`, try values), then persists: `ssh $SAT 'sudo alsactl store'`.

- [ ] **Step 7: Bake the calibrated default (only if ≠ 60)**

If the chosen value differs from 60, update both occurrences in `scripts/provision-satellite-rs.sh` (`TTS_VOLUME:-60` on the ssh line; `default 60` in the header comment) and the two doc mentions from Task 3, then:

```bash
git add scripts/provision-satellite-rs.sh satellite/deploy/nabu-satellite.service satellite/CLAUDE.md
git commit -m "chore(satellite): bake calibrated TTS_VOLUME default

Claude-Session: https://claude.ai/code/session_01ApDq2P4YRNQBJzfRU4sRUr"
```

- [ ] **Step 8: Reboot persistence**

```bash
ssh $SAT 'sudo reboot'
# wait ~60 s
ssh $SAT 'amixer -c sndrpihifiberry sget TTS; systemctl is-active nabu-satellite nabu-micclock snapclient'
```

Expected: calibrated % retained; services active. Say "ok nabu" once more → wake + reply still work at the calibrated level.
