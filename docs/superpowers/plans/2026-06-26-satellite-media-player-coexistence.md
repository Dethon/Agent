# Satellite Media Player (dmix Coexistence) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make each Rust `nabu-satellite` a Home-Assistant-controllable media player by adding a Music Assistant + Snapcast stack and letting `snapclient` share the Jabra speaker via an ALSA `dmix`/`softvol` mixer, with the satellite ducking music under its own speech — without regressing the 2026-06-26 Jabra audio fixes.

**Architecture:** Music Assistant (stock container, built-in Snapcast server) streams to a `snapclient` on each Pi that plays into an ALSA `softvol` PCM (`music`) feeding a shared `dmix` PCM (`duckmix`) over the Jabra card. The satellite's TTS/cues play to `duckmix` directly while `arecord` stays on direct `plughw` (capture untouched). A new per-connection "duck task" in the satellite mirrors the existing LED render task: it watches the same `LedState` channel and sets the `Music` softvol via `amixer` — full when `Idle`, ducked when active.

**Tech Stack:** Rust (`nabu-satellite`, `pico_args`, `tokio`, raw-ALSA subprocesses), ALSA `dmix`/`softvol`, Snapcast (`snapclient`), Music Assistant (`ghcr.io/music-assistant/server`), Docker Compose, .NET 10 (Observability `BackgroundService`, `Domain/Prompts`).

## Global Constraints

- **No new agent env var or secret.** Spotify auth lives only in Music Assistant's own data volume (OAuth). The single new config key is `Observability/appsettings.json` → `HttpProbes` (non-secret, compose-internal URL); it is NOT an env var, NOT a `.env` entry, and NOT agent config.
- **Satellite is a separate Rust crate** (`satellite/`), NOT in `agent.sln`. Build/test it with `--manifest-path satellite/Cargo.toml`. Unit tests run host-native (no cross-compile needed).
- **CLI flags use `pico_args`, not clap** — add valued flags with `pa.opt_value_from_str(...)` BEFORE `pa.finish()`.
- **Testability DI seam is `enum { Real(...), #[cfg(test)] Probe(Arc<Mutex<...>>) }`** — NO `dyn Trait`, no new crates (mirror `LedBackend` in `satellite/src/led.rs`).
- **All satellite subprocesses go through `crate::audio::build_command(&str)`** (plain-argv → direct exec; metacharacters → `sh -c`). `amixer -c <card> sset <ctl> <pct>%` is plain-argv.
- **tokio task `abort()` skips async cleanup** — the fail-safe "restore music to 100%" must run in a synchronous `Drop`, never in an `async` block after an `.await`.
- **No trailing newline in any `.cs` file** (`.editorconfig` + pre-commit `dotnet format` re-stages whole files; never split a `.cs` change across a hunk stage).
- **Do NOT touch** `McpChannelVoice`'s audio path or the satellite's **capture** (`arecord`) path — the capture path stays byte-for-byte unchanged. Note: the play-to-wake tone is emitted on the **playback** path (the snd command), not the capture path; for music units it flows through `duckmix` along with TTS and cues, remaining at full scale (softvol is only on the `music` branch). This is intentional and must be validated on-device (cold-boot Jabra wake through `dmix`).
- **`docker-compose.yml` conventions:** `<<: *timezone` inside `environment:`, `restart: unless-stopped`, `logging: max-size 5m/max-file 3`, `networks: - jackbot`, `container_name` == service name. Named volumes are bare keys under top-level `volumes:`.
- Commit after each task. The pre-commit hook runs `dotnet format` over staged `.cs` files; append your session's commit trailer per repo convention.

---

## File Structure

**New files**
- `satellite/src/music.rs` — duck feature: `DuckerBackend` enum (Real/Probe), `target_percent`, `duck_loop`, `spawn_duck`, `DuckGuard` (fail-safe restore on Drop), and `#[cfg(test)] mod tests`.
- `satellite/deploy/snapclient.service` — systemd unit template for the per-Pi Snapcast music client.
- `Observability/Services/HttpHealthProbeService.cs` — `BackgroundService` that HTTP-probes stock containers and writes the same Redis health keys + SignalR event `MetricsCollectorService` uses.

**Modified files**
- `satellite/src/config.rs` — add `music_mixer`/`music_card`/`duck_percent` fields, defaults, parse lines, and config tests.
- `satellite/src/main.rs` — add `mod music;`.
- `satellite/src/satellite/state_machine.rs` — subscribe a 2nd `LedState` receiver and spawn the duck guard.
- `satellite/deploy/nabu-satellite.service` — add a `__MUSIC_FLAGS__` placeholder line to `ExecStart`.
- `scripts/provision-satellite-rs.sh` — opt-in (`MUSIC_HUB` set) music provisioning: install `snapclient`, write `/etc/asound.conf`, retarget the satellite's `--snd-command` to `duckmix`, inject `--music-mixer`/`--music-card`, install the `snapclient` unit.
- `DockerCompose/docker-compose.yml` — new `music-assistant` service + `music-assistant-data` named volume.
- `CLAUDE.md` — append `music-assistant` to both launch commands; add a music-satellite note.
- `Domain/Prompts/HomeAssistantPrompt.cs` — new `### Music playback` section in the `SystemPrompt` const.
- `Tests/Unit/Domain/Prompts/HomeAssistantPromptTests.cs` — new prompt-content `[Fact]`.
- `Observability/Program.cs` — register `AddHttpClient()` + `AddHostedService<HttpHealthProbeService>()`.
- `Observability/appsettings.json` — `HttpProbes` section.

**Task dependency order:** Task 1 (compose) is independent. Tasks 2→3→4 are the satellite ducking feature (sequential). Task 5 (provisioning) depends conceptually on Tasks 1–4 existing. Tasks 6 and 7 (polish) are independent of everything else.

---

### Task 1: `music-assistant` compose service + launch commands

**Files:**
- Modify: `DockerCompose/docker-compose.yml` (add a service; add a named volume)
- Modify: `CLAUDE.md:120` and `CLAUDE.md:123` (append `music-assistant` to both launch commands)

**Interfaces:**
- Produces: a `music-assistant` service on the `jackbot` network, web/API at `http://music-assistant:8095`, Snapcast ports `1704/1705/1780`, data in named volume `music-assistant-data`. Task 5's `snapclient` dials `<hub-host>:1704`; Task 7's probe hits `http://music-assistant:8095/`.

- [ ] **Step 1: Add the `music-assistant` service.** Insert this service block adjacent to the `homeassistant` service in `DockerCompose/docker-compose.yml` (match surrounding indentation exactly):

```yaml
  music-assistant:
    image: ghcr.io/music-assistant/server:latest
    logging:
      options:
        max-size: "5m"
        max-file: "3"
    container_name: music-assistant
    ports:
      - "8095:8095"     # web UI + API/websocket (HA Music Assistant integration target)
      - "1704:1704"     # snapcast stream  (satellite snapclients connect here)
      - "1705:1705"     # snapcast control
      - "1780:1780"     # snapcast web
    volumes:
      - music-assistant-data:/data
    environment:
      <<: *timezone
    restart: unless-stopped
    networks:
      - jackbot
```

- [ ] **Step 2: Declare the named volume.** Under the top-level `volumes:` block at the end of `DockerCompose/docker-compose.yml` (where `printer-spool:` lives), add:

```yaml
  music-assistant-data:
```

- [ ] **Step 3: Append `music-assistant` to both launch commands in `CLAUDE.md`.** On line 120 (Linux) and line 123 (Windows), both currently end with `... camoufox homeassistant`. Change each trailing token list to end `... camoufox homeassistant music-assistant`.

- [ ] **Step 4: Validate the compose file parses and merges.**

Run: `docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot config -q`
Expected: exits 0 with no YAML/merge errors (warnings about unset `${...}` env vars are fine). If `docker` is unavailable, instead run `python3 -c "import yaml,sys; yaml.safe_load(open('DockerCompose/docker-compose.yml'))"` (exits 0).

- [ ] **Step 5: Commit.**

```bash
git add DockerCompose/docker-compose.yml CLAUDE.md
git commit -m "feat(compose): add music-assistant service for satellite media player"
```

---

### Task 2: Satellite CLI flags — `--music-mixer`, `--music-card`, `--duck-percent`

**Files:**
- Modify: `satellite/src/config.rs` (struct fields, `Default`, `parse`, tests)

**Interfaces:**
- Produces: `Config.music_mixer: Option<String>` (None ⇒ duck feature off), `Config.music_card: Option<String>` (amixer `-c` target), `Config.duck_percent: u8` (default 20). Consumed by Tasks 3 & 4.

- [ ] **Step 1: Write the failing config-parse test.** In the `#[cfg(test)] mod tests` block of `satellite/src/config.rs`, add:

```rust
#[test]
fn music_flags_parse_and_default_off() {
    let on = Config::parse(pico_args::Arguments::from_vec(vec![
        "--music-mixer".into(), "Music".into(),
        "--music-card".into(), "Jabra".into(),
        "--duck-percent".into(), "15".into(),
    ]))
    .unwrap();
    assert_eq!(on.music_mixer.as_deref(), Some("Music"));
    assert_eq!(on.music_card.as_deref(), Some("Jabra"));
    assert_eq!(on.duck_percent, 15);

    let off = Config::parse(pico_args::Arguments::from_vec(vec![])).unwrap();
    assert_eq!(off.music_mixer, None);
    assert_eq!(off.duck_percent, 20); // default
}
```

- [ ] **Step 2: Run it to verify it fails.**

Run: `cargo test --manifest-path satellite/Cargo.toml --lib config::tests::music_flags_parse_and_default_off`
Expected: FAIL — compile error `no field music_mixer on type Config` (and `music_card`/`duck_percent`).

- [ ] **Step 3: Add the fields, defaults, and parse lines.** In the `Config` struct add:

```rust
    pub music_mixer: Option<String>,   // ALSA softvol control name; None => duck feature off
    pub music_card: Option<String>,    // amixer -c target where the softvol control lives
    pub duck_percent: u8,              // softvol level while the satellite is active
```

In the `Default for Config` impl add:

```rust
            music_mixer: None,
            music_card: None,
            duck_percent: 20,
```

In `parse`, immediately before `let rest = pa.finish();`, add:

```rust
        if let Some(v) = pa.opt_value_from_str::<_, String>("--music-mixer")? { c.music_mixer = Some(v); }
        if let Some(v) = pa.opt_value_from_str::<_, String>("--music-card")? { c.music_card = Some(v); }
        if let Some(v) = pa.opt_value_from_str::<_, u8>("--duck-percent")? { c.duck_percent = v; }
```

- [ ] **Step 4: Run the test to verify it passes.**

Run: `cargo test --manifest-path satellite/Cargo.toml --lib config::tests::music_flags_parse_and_default_off`
Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add satellite/src/config.rs
git commit -m "feat(satellite): add --music-mixer/--music-card/--duck-percent flags"
```

---

### Task 3: Satellite music-duck module (`music.rs`)

**Files:**
- Create: `satellite/src/music.rs`
- Test: inline `#[cfg(test)] mod tests` in `satellite/src/music.rs`

**Interfaces:**
- Consumes: `crate::led::LedState` (enum `Idle|Listening|Thinking|Speaking`), `crate::audio::build_command`.
- Produces: `pub fn spawn_duck(rx: tokio::sync::watch::Receiver<LedState>, music_mixer: Option<String>, music_card: Option<String>, duck_percent: u8) -> Option<DuckGuard>`; `pub struct DuckGuard` (aborts the task and restores the control to 100% on Drop). Internal: `fn target_percent(state: LedState, duck_percent: u8) -> u8`, `async fn duck_loop(...)`, `enum DuckerBackend`. Wired in by Task 4.

- [ ] **Step 1: Write the failing tests.** Create `satellite/src/music.rs` containing ONLY the test module first (so the test names exist and fail to compile against the not-yet-written items):

```rust
#[cfg(test)]
mod tests {
    use super::*;
    use crate::led::LedState;
    use std::sync::{Arc, Mutex};
    use tokio::sync::watch;

    fn probe() -> (Arc<Mutex<Vec<u8>>>, DuckerBackend) {
        let log = Arc::new(Mutex::new(Vec::new()));
        (log.clone(), DuckerBackend::Probe(log))
    }

    #[test]
    fn idle_is_full_active_is_ducked() {
        assert_eq!(target_percent(LedState::Idle, 20), 100);
        assert_eq!(target_percent(LedState::Listening, 20), 20);
        assert_eq!(target_percent(LedState::Thinking, 20), 20);
        assert_eq!(target_percent(LedState::Speaking, 20), 20);
        assert_eq!(target_percent(LedState::Listening, 35), 35);
    }

    async fn wait_for(log: &Arc<Mutex<Vec<u8>>>, len: usize) {
        for _ in 0..1000 {
            if log.lock().unwrap().len() >= len { return; }
            tokio::task::yield_now().await;
        }
        panic!("timed out waiting for duck call #{len}; got {:?}", log.lock().unwrap());
    }

    #[tokio::test]
    async fn ducks_on_active_dedups_and_restores_on_idle() {
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20));

        wait_for(&log, 1).await;                 // initial Idle -> 100
        assert_eq!(log.lock().unwrap()[0], 100);

        tx.send(LedState::Listening).unwrap();
        wait_for(&log, 2).await;                 // -> 20
        assert_eq!(log.lock().unwrap()[1], 20);

        tx.send(LedState::Speaking).unwrap();    // active -> active, same 20: deduped (no new call)
        tx.send(LedState::Idle).unwrap();
        wait_for(&log, 3).await;                 // -> 100
        assert_eq!(log.lock().unwrap()[2], 100);

        drop(tx);
        let _ = h.await;
        assert_eq!(log.lock().unwrap().len(), 3, "active->active must not re-issue an amixer call");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

Run: `cargo test --manifest-path satellite/Cargo.toml --lib music::tests`
Expected: FAIL — compile errors (`target_percent`, `duck_loop`, `DuckerBackend` not found).

- [ ] **Step 3: Write the implementation.** Prepend (above the test module) in `satellite/src/music.rs`:

```rust
use crate::led::LedState;
use tokio::sync::watch;
use tracing::warn;

enum DuckerBackend {
    Real { control: String, card: Option<String> },
    #[cfg(test)]
    Probe(std::sync::Arc<std::sync::Mutex<Vec<u8>>>),
}

impl DuckerBackend {
    async fn set(&mut self, pct: u8) -> anyhow::Result<()> {
        match self {
            DuckerBackend::Real { control, card } => {
                let cmd = match card {
                    Some(c) => format!("amixer -c {c} sset {control} {pct}%"),
                    None => format!("amixer sset {control} {pct}%"),
                };
                let status = crate::audio::build_command(&cmd)
                    .stdout(std::process::Stdio::null())
                    .stderr(std::process::Stdio::null())
                    .status()
                    .await?;
                anyhow::ensure!(status.success(), "amixer exited with {status}");
                Ok(())
            }
            #[cfg(test)]
            DuckerBackend::Probe(log) => {
                log.lock().unwrap().push(pct);
                Ok(())
            }
        }
    }
}

fn target_percent(state: LedState, duck_percent: u8) -> u8 {
    if state == LedState::Idle { 100 } else { duck_percent }
}

async fn duck_loop(mut rx: watch::Receiver<LedState>, mut backend: DuckerBackend, duck_percent: u8) {
    let mut applied: Option<u8> = None;
    loop {
        let pct = target_percent(*rx.borrow_and_update(), duck_percent);
        if applied != Some(pct) {
            if let Err(e) = backend.set(pct).await {
                warn!("music duck failed, ducking disabled for this connection: {e:#}");
                return;
            }
            applied = Some(pct);
        }
        if rx.changed().await.is_err() {
            break; // sender dropped => connection ending; DuckGuard::drop restores to full
        }
    }
}

pub struct DuckGuard {
    handle: tokio::task::JoinHandle<()>,
    control: String,
    card: Option<String>,
}

impl Drop for DuckGuard {
    fn drop(&mut self) {
        self.handle.abort();
        // Fail-safe restore to full volume. abort() drops the task future at its await point and
        // skips async cleanup, so this MUST be synchronous: fire a detached std amixer (never awaited).
        let mut cmd = std::process::Command::new("amixer");
        if let Some(c) = &self.card {
            cmd.arg("-c").arg(c);
        }
        cmd.args(["sset", &self.control, "100%"])
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null());
        let _ = cmd.spawn();
    }
}

pub fn spawn_duck(
    rx: watch::Receiver<LedState>,
    music_mixer: Option<String>,
    music_card: Option<String>,
    duck_percent: u8,
) -> Option<DuckGuard> {
    let control = music_mixer?; // None => feature off (mirrors led::spawn_led returning None)
    let backend = DuckerBackend::Real { control: control.clone(), card: music_card.clone() };
    let handle = tokio::spawn(duck_loop(rx, backend, duck_percent));
    Some(DuckGuard { handle, control, card: music_card })
}
```

(If `LedState` is not `PartialEq`, add `#[derive(..., PartialEq, Eq)]` to its definition in `satellite/src/led.rs` — `render_loop` already compares it with `!=`, so it is almost certainly already `PartialEq`; verify and only add if the compiler complains. If `tracing::warn` is not the crate's logging macro, match whatever `led.rs` uses — it uses `warn!` from `tracing`.)

- [ ] **Step 4: Run the tests to verify they pass.**

Run: `cargo test --manifest-path satellite/Cargo.toml --lib music::tests`
Expected: PASS (both `idle_is_full_active_is_ducked` and `ducks_on_active_dedups_and_restores_on_idle`).

- [ ] **Step 5: Lint.**

Run: `cargo clippy --manifest-path satellite/Cargo.toml --all-targets`
Expected: no new warnings on `music.rs`. (`music.rs` is not yet referenced by `main.rs`, so expect a `dead_code`/unused-module note only if the module is declared; it is declared in Task 4. Until then clippy may not see it — that's fine.)

- [ ] **Step 6: Commit.**

```bash
git add satellite/src/music.rs
git commit -m "feat(satellite): music duck module (state->softvol mapping, fail-safe restore)"
```

---

### Task 4: Wire the duck task into the connection lifecycle

**Files:**
- Modify: `satellite/src/main.rs` (add `mod music;`)
- Modify: `satellite/src/satellite/state_machine.rs` (subscribe a 2nd receiver; spawn the duck guard)

**Interfaces:**
- Consumes: `crate::music::spawn_duck`, `Config.music_mixer`/`music_card`/`duck_percent` (Task 2), the existing `watch::channel(LedState::Idle)` at `state_machine.rs:106`.
- Produces: a per-connection `DuckGuard` local dropped (→ task aborted + music restored) whenever `run_connection` returns or is aborted by `main.rs`'s supersede policy.

- [ ] **Step 1: Declare the module.** In `satellite/src/main.rs`, alongside the other top-level `mod` declarations (e.g. next to `mod led;`), add:

```rust
mod music;
```

- [ ] **Step 2: Subscribe a second receiver and spawn the duck guard.** In `satellite/src/satellite/state_machine.rs`, find the channel creation (around line 106):

```rust
    let (led_tx, led_rx) = watch::channel(LedState::Idle);
    let _led_guard = led::spawn_led(&cfg.led, led_rx);
```

Change it to subscribe a second receiver from the sender BEFORE `led_rx` is moved into `spawn_led`, and spawn the duck guard right after:

```rust
    let (led_tx, led_rx) = watch::channel(LedState::Idle);
    let duck_rx = led_tx.subscribe();
    let _led_guard = led::spawn_led(&cfg.led, led_rx);
    let _duck_guard = crate::music::spawn_duck(
        duck_rx,
        cfg.music_mixer.clone(),
        cfg.music_card.clone(),
        cfg.duck_percent,
    );
```

`_duck_guard` is `Option<DuckGuard>`; as a local in `run_connection` it drops (aborting the task and restoring music to 100%) on every return path and on abort — exactly like `_led_guard`. Do not move it into `Ctx`.

- [ ] **Step 3: Build the satellite.**

Run: `cargo build --manifest-path satellite/Cargo.toml`
Expected: builds clean (`music` module now referenced; no dead-code warning).

- [ ] **Step 4: Run the whole inline suite to confirm nothing regressed.**

Run: `cargo test --manifest-path satellite/Cargo.toml --lib`
Expected: PASS — all existing tests plus the two new `music::tests` and the new `config::tests` test.

- [ ] **Step 5: Lint.**

Run: `cargo clippy --manifest-path satellite/Cargo.toml --all-targets`
Expected: no new warnings.

- [ ] **Step 6: Commit.**

```bash
git add satellite/src/main.rs satellite/src/satellite/state_machine.rs
git commit -m "feat(satellite): drive music ducking from the connection state channel"
```

---

### Task 5: Provisioning — dmix/softvol + snapclient (opt-in via `MUSIC_HUB`)

**Files:**
- Modify: `satellite/deploy/nabu-satellite.service` (add a `__MUSIC_FLAGS__` placeholder line)
- Create: `satellite/deploy/snapclient.service`
- Modify: `scripts/provision-satellite-rs.sh` (opt-in music block + per-line device templating)

**Interfaces:**
- Consumes: Task 2/3/4's `--music-mixer`/`--music-card` flags; Task 1's hub Snapcast port `1704`.
- Produces: on a Pi provisioned with `MUSIC_HUB`/`MUSIC_ROOM` set — `/etc/asound.conf` (`duckmix` dmix + `music` softvol), the satellite playing TTS to `duckmix` with `--music-mixer Music --music-card <name>`, and a running `snapclient` feeding `music`. With `MUSIC_HUB` unset the script behaves exactly as today (pure voice unit).

> **Note:** This task's deliverable is scripts/units, validated by `bash -n` syntax check and reviewed against the on-device gate in the Operator Checklist (a Pi is required for real verification — there is no unit test). Music provisioning is supported only on the **auto-detected USB-card path** (no explicit `[mic-device]` arg), since it needs the detected card name; setting both `MIC` and `MUSIC_HUB` must error out.

- [ ] **Step 1: Add the music-flags placeholder to the satellite unit.** In `satellite/deploy/nabu-satellite.service`, in the `ExecStart` block, add a placeholder line immediately after the `--keep-warm \` line and before `--threshold 0.4`:

```
  --keep-warm \
  __MUSIC_FLAGS__ \
  --threshold 0.4
```

(The provisioning script either substitutes or deletes this line, so a non-music unit is byte-identical to today.)

- [ ] **Step 2: Create the snapclient unit template** `satellite/deploy/snapclient.service`:

```ini
# Snapcast music client for a nabu satellite. Provisioning rewrites HUBHOST/ROOM/%i.
# --soundcard music => the softvol PCM in /etc/asound.conf, so the satellite can duck it via amixer.
# --hostID ROOM     => the Music Assistant / Snapcast player name; match it to the HA area slug
#                      so /ha/areas/<room>/ resolves media_player.<room>.
[Unit]
Description=Snapcast music client for nabu satellite (%i)
After=network-online.target sound.target nabu-satellite.service
Wants=network-online.target

[Service]
ExecStart=/usr/bin/snapclient --host HUBHOST --port 1704 --hostID ROOM --player alsa --soundcard music
Restart=always
RestartSec=2
User=%i
SupplementaryGroups=audio

[Install]
WantedBy=multi-user.target
```

- [ ] **Step 3: Add the opt-in music block to `scripts/provision-satellite-rs.sh`.** Two edits.

(a) Reject the unsupported `MIC + MUSIC_HUB` combination. Near the top of the remote heredoc (after `set -euo pipefail`), add:

```bash
  if [ -n "${MUSIC_HUB:-}" ] && [ -n "${MIC}" ]; then
    echo "ERROR: music provisioning (MUSIC_HUB) supports only the auto-detected USB card; do not also pass a mic-device." >&2
    exit 1
  fi
```

(b) Replace the current unit-templating tail (the `sudo sed ... | sudo tee /etc/systemd/system/nabu-satellite.service` step and the apt line) so it installs `snapclient`, writes `asound.conf`, retargets the playback device, and installs the snapclient unit when `MUSIC_HUB` is set. Use this structure (the `$cardname`/`$cardidx`/`$dev` variables already exist from the auto-detect branch):

```bash
  # --- music coexistence (opt-in via MUSIC_HUB) ---
  snddev="${dev}"          # default: same device as capture (voice-only unit, unchanged)
  music_sed=(-e "/__MUSIC_FLAGS__/d")   # default: strip the placeholder line entirely
  if [ -n "${MUSIC_HUB:-}" ]; then
    sudo apt-get install -y snapclient
    snddev="duckmix"
    music_sed=(-e "s|__MUSIC_FLAGS__|--music-mixer Music --music-card ${cardname}|")

    # Shared dmix + softvol over the Jabra card, by NAME. NO pcm.!default: arecord keeps its
    # explicit plughw capture device, so the wake-tone path is untouched.
    sudo tee /etc/asound.conf >/dev/null <<ASOUND
pcm.duckmix {
    type plug
    slave.pcm {
        type dmix
        ipc_key 32421
        ipc_perm 0660
        slave {
            pcm "hw:CARD=${cardname},DEV=0"
            format S16_LE
            rate 48000
            channels 2
        }
    }
}
pcm.music {
    type plug
    slave.pcm {
        type softvol
        slave.pcm "duckmix"
        control { name "Music" card ${cardname} }
        min_dB -51.0
        max_dB 0.0
        resolution 256
    }
}
ASOUND

    # snapclient unit -> hub Snapcast :1704, output into the softvol PCM.
    sudo usermod -aG audio "$user"
    sudo sed "s/%i/$user/g; s/HUBHOST/${MUSIC_HUB}/g; s/ROOM/${MUSIC_ROOM:-$cardname}/g" \
      /tmp/snapclient.service | sudo tee /etc/systemd/system/snapclient.service >/dev/null
    sudo systemctl daemon-reload
    sudo systemctl enable snapclient.service
    sudo systemctl restart snapclient.service
  fi

  # Template the satellite unit: mic device stays plughw (capture untouched), snd device may be
  # duckmix (music) or plughw (voice), %i -> user, and the music flags line substituted/stripped.
  sudo sed -e "/arecord/ s#plughw:0,0#${dev}#" \
           -e "/aplay/  s#plughw:0,0#${snddev}#" \
           -e "s/%i/$user/g" \
           "${music_sed[@]}" \
           /tmp/nabu-satellite.service | sudo tee /etc/systemd/system/nabu-satellite.service >/dev/null
```

Also: (c) `scp` the new unit. Next to the existing `scp ... nabu-satellite.service`, add (outside the heredoc, in the local part of the script):

```bash
scp "${SSHOPTS[@]}" "$(dirname "$0")/../satellite/deploy/snapclient.service" "$host:/tmp/"
```

And (d) remove the now-obsolete single `sudo sed "s/%i/$user/g; s#plughw:0,0#${dev}#g" ...` line that this block replaces, plus keep the original `apt-get install -y alsa-utils` line as-is (snapclient is added conditionally above).

- [ ] **Step 4: Syntax-check the script.**

Run: `bash -n scripts/provision-satellite-rs.sh`
Expected: exits 0 (no syntax error). If `shellcheck` is installed, also run `shellcheck scripts/provision-satellite-rs.sh` and address any new error (warnings about the heredoc-embedded remote vars are expected/acceptable).

- [ ] **Step 5: Update the unit's header comment** in `satellite/deploy/nabu-satellite.service` to document the music path (mirror the existing comment style):

```
# Music satellites (provision with MUSIC_HUB=<hub-host> MUSIC_ROOM=<room>) additionally get an
# /etc/asound.conf dmix+softvol mixer; --snd-command targets the `duckmix` PCM and the unit carries
# --music-mixer Music --music-card <name>, so the satellite ducks the `music` softvol (snapclient)
# while it speaks. Capture (arecord) stays on direct plughw — the wake-tone path is unchanged.
```

- [ ] **Step 6: Commit.**

```bash
git add scripts/provision-satellite-rs.sh satellite/deploy/snapclient.service satellite/deploy/nabu-satellite.service
git commit -m "feat(satellite): provision dmix/softvol + snapclient for music coexistence"
```

---

### Task 6: HomeAssistant prompt — music playback guidance

**Files:**
- Modify: `Domain/Prompts/HomeAssistantPrompt.cs` (new `### Music playback` section inside `SystemPrompt`)
- Test: `Tests/Unit/Domain/Prompts/HomeAssistantPromptTests.cs` (new `[Fact]`)

**Interfaces:**
- Produces: static prompt text teaching `music_assistant.play_media` (search-by-name) and `media_player.join`/`media_player.unjoin`, defaulting to the speaking room. Unconditionally included (the const is always the base of the MCP prompt).

- [ ] **Step 1: Write the failing prompt-content test.** In `Tests/Unit/Domain/Prompts/HomeAssistantPromptTests.cs` add:

```csharp
    [Fact]
    public void SystemPrompt_TeachesMusicPlaybackAndGroupingIdiom()
    {
        var prompt = HomeAssistantPrompt.SystemPrompt;

        prompt.ShouldContain("Music playback");
        prompt.ShouldContain("music_assistant.play_media");
        prompt.ShouldContain("media_player.join");
        prompt.ShouldContain("media_player.unjoin");
        prompt.ShouldContain("speaking room"); // default target is the room the request came from
    }
```

- [ ] **Step 2: Run it to verify it fails.**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantPromptTests"`
Expected: FAIL — `SystemPrompt_TeachesMusicPlaybackAndGroupingIdiom` fails its first `ShouldContain` (the existing alarm test still passes).

- [ ] **Step 3: Add the guidance section.** In `Domain/Prompts/HomeAssistantPrompt.cs`, inside the `SystemPrompt` raw string literal, insert a new section between `### Alarms & reminders` and `### Notes` (match the existing 8-space indentation exactly):

```
        ### Music playback
        Each room's satellite is a `media_player.<room>` (a Music Assistant / Snapcast player in
        that HA area). To play music, run the player's action via `/ha`:
        - Play by name (artist/track/playlist/radio): `music_assistant.play_media` with
          `media_id` set to the search text, e.g. `media_id: "miles davis"`. Default the target to
          the **speaking room** (`media_player.<room>` for the room the request came from) unless
          another room is named; "everywhere" => target all room players.
        - Transport: `media_player.media_play` / `media_pause` / `media_next_track` /
          `volume_set` on the target player.
        - Grouping (synced multi-room): `media_player.join` (target = one player, `group_members` =
          the others) to play in sync; `media_player.unjoin` to split a room back out.
        Music ducks automatically while the satellite speaks — never lower or pause music just to
        talk.
```

- [ ] **Step 4: Run the test to verify it passes.**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantPromptTests"`
Expected: PASS — both the existing alarm test and the new music test.

- [ ] **Step 5: Commit.**

```bash
git add Domain/Prompts/HomeAssistantPrompt.cs Tests/Unit/Domain/Prompts/HomeAssistantPromptTests.cs
git commit -m "feat(prompt): teach music_assistant play_media + media_player grouping"
```

---

### Task 7: Observability — `music-assistant` health tile via HTTP probe

**Files:**
- Create: `Observability/Services/HttpHealthProbeService.cs`
- Modify: `Observability/Program.cs:18` (register `AddHttpClient()` + the hosted service)
- Modify: `Observability/appsettings.json` (`HttpProbes` section)

**Interfaces:**
- Consumes: `IConnectionMultiplexer`, `IHubContext<MetricsHub>` (`Observability/Hubs/MetricsHub.cs`), `IHttpClientFactory`, `IConfiguration`; the Observability-side `ServiceHealthUpdate(string Service, bool IsHealthy, DateTimeOffset Timestamp)` record and the Redis health keys (`metrics:health:{service}` 60 s TTL, set `metrics:health:known`) + SignalR event `"OnHealthUpdate"` that `MetricsCollectorService.ProcessHeartbeatAsync` uses.
- Produces: a green `music-assistant` tile in `HealthGrid` (it appears automatically once the name is in `metrics:health:known`).

> **Note:** This is infra glue (a `BackgroundService` doing HTTP + Redis I/O), verified by build and by the on-device tile check in the Operator Checklist — there is no isolated unit test (matching the spec's infra-first testing posture). It replicates `ProcessHeartbeatAsync` semantics directly because Observability cannot reference `Infrastructure`/`IMetricsPublisher`.

- [ ] **Step 1: Create the probe service** `Observability/Services/HttpHealthProbeService.cs`:

```csharp
using Microsoft.AspNetCore.SignalR;
using Observability.Hubs;
using StackExchange.Redis;

namespace Observability.Services;

public sealed class HttpHealthProbeService(
    IHttpClientFactory httpClientFactory,
    IConnectionMultiplexer redis,
    IHubContext<MetricsHub> hubContext,
    IConfiguration configuration,
    ILogger<HttpHealthProbeService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeyTtl = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var targets = configuration.GetSection("HttpProbes").GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => (Service: c.Key, Url: c.Value!))
            .ToArray();
        if (targets.Length == 0)
            return;

        var http = httpClientFactory.CreateClient();
        var db = redis.GetDatabase();

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (service, url) in targets)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    // Any HTTP response (even non-2xx) means the container is up and listening.
                    using var _ = await http.GetAsync(url, cts.Token);
                    await MarkHealthyAsync(db, service, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "health probe for {Service} at {Url} failed", service, url);
                }
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task MarkHealthyAsync(IDatabase db, string service, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await Task.WhenAll(
            db.StringSetAsync($"metrics:health:{service}", now.ToString("o"), KeyTtl),
            db.SetAddAsync("metrics:health:known", service));
        await hubContext.Clients.All.SendAsync(
            "OnHealthUpdate", new ServiceHealthUpdate(service, true, now), ct);
    }
}
```

(If `ServiceHealthUpdate` is not visible from this file, confirm its namespace in `Observability/Services/MetricsCollectorService.cs` (the research located it there) and add the matching `using`. If `ILogger`/`IConfiguration` need a `using`, they live in `Microsoft.Extensions.Logging`/`Microsoft.Extensions.Configuration` — `ImplicitUsings` likely covers them.)

- [ ] **Step 2: Register the service.** In `Observability/Program.cs`, immediately after line 18 (`builder.Services.AddHostedService<MetricsCollectorService>();`) add:

```csharp
builder.Services.AddHttpClient();
builder.Services.AddHostedService<HttpHealthProbeService>();
```

- [ ] **Step 3: Add the probe target to config.** In `Observability/appsettings.json`, add a top-level section (non-secret, compose-internal URL):

```json
  "HttpProbes": {
    "music-assistant": "http://music-assistant:8095/"
  }
```

- [ ] **Step 4: Build to verify it compiles.**

Run: `dotnet build Observability/Observability.csproj`
Expected: build succeeded, 0 errors. (Resolve any `ServiceHealthUpdate`/hub-type `using` issue here.)

- [ ] **Step 5: Commit.**

```bash
git add Observability/Services/HttpHealthProbeService.cs Observability/Program.cs Observability/appsettings.json
git commit -m "feat(observability): music-assistant health tile via HTTP probe"
```

---

## Operator Checklist (manual — not code; cannot be scripted)

These steps require the running stack and physical hardware. They are the real correctness gates for the infra-first parts.

1. **Bring up Music Assistant:** add `music-assistant` per Task 1, `docker compose ... up -d music-assistant`. Confirm the web UI at `http://localhost:8095` (or via Caddy) loads.
2. **HA integration:** add the **Music Assistant** integration in Home Assistant → `http://music-assistant:8095`. In MA, add the **Spotify** provider (Premium OAuth, in MA's own UI) and the **Snapcast** player provider (built-in server). Spotify auth never leaves MA's data volume.
3. **Name players to room slugs:** name each MA/Snapcast player after its room and assign it to the matching HA **area** (e.g. satellite `Room: Kitchen` ↔ area `kitchen` ↔ `media_player.kitchen`), so `/ha/areas/<room>/` resolves the right player.
4. **Provision a Pi (music):** `MUSIC_HUB=<hub-host> MUSIC_ROOM=<room> scripts/provision-satellite-rs.sh <user@pi>`.
5. **Phase-2 GATE (do this BEFORE trusting the mixer):** with `dmix`/`softvol` installed and the satellite's `aplay` on `duckmix`, speak to the satellite and play a cue/wake — confirm **TTS onset, cue beeps, and keep-warm sound identical to a voice-only unit**. If onset is degraded, tune `--start-delay`/`-F` (and the `dmix` `period`/`buffer`) before proceeding. Capture (arecord) is on direct `plughw` and is unaffected. **Critical on-device gate (highest-risk unknown):** after a full **host reboot** (Jabra mic ADC in firmware deep sleep), confirm the satellite still wakes the mic via the play-to-wake tone **through `dmix`** — the tone now traverses `duckmix` for music units, so cold-boot wake-from-deep-sleep through the shared mixer must be explicitly proven before declaring Phase 2 complete.
6. **Coexistence:** play a Spotify track to the Pi from MA/HA → audio plays via `snapclient`. Speak → music **ducks to `--duck-percent`** and **restores** on idle. Wake while music plays → wake/STT still work (listen-window duck on `Listening`).
7. **Disconnect/supersede restore:** kill/replace the hub connection while music is ducked → confirm `DuckGuard` restored music to full.
8. **Health tile:** with the stack up, confirm a green `music-assistant` tile appears in the dashboard `HealthGrid` within ~45 s; stop the container → tile goes red within ~75 s.

## Deferred (out of this plan — needs ≥2 Pis)

- **Slice 5: synced multi-room** — `media_player.join`/`unjoin` across two provisioned Pis; verify Snapcast sync and tune `snapclient` buffers (`--player alsa:buffer_time=...` in `snapclient.service`). Re-run the coexistence gate per room (speaking ducks only the addressed room).

## Self-Review

- **Spec coverage:** §dmix topology → Task 5 (asound.conf) + Task 4 (snd→duckmix via provisioning). §satellite ducking → Tasks 2–4. §"preserve 2026-06-26 fixes" → Task 5 keeps `arecord`/wake-tone on `plughw`, keeps `--keep-warm`, by-name card; Operator gate 5 revalidates onset. §compose infra → Task 1. §HomeAssistantPrompt polish → Task 6. §HealthGrid polish → Task 7. §manual MA/HA/on-device → Operator Checklist. §multi-room → Deferred. All spec sections map to a task or an explicit operator/deferred item.
- **Placeholder scan:** every code step shows complete code; the only "confirm-this-symbol" notes (LedState PartialEq; ServiceHealthUpdate using) reference existing repo types, not undefined ones.
- **Type consistency:** `spawn_duck(rx, Option<String> music_mixer, Option<String> music_card, u8 duck_percent) -> Option<DuckGuard>` used identically in Task 3 (def) and Task 4 (call); `Config.music_mixer/music_card/duck_percent` (Task 2) match their reads (Task 4); `ServiceHealthUpdate(string,bool,DateTimeOffset)` matches the verified record.
