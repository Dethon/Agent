# STT accuracy on short phrases

Date: 2026-08-01

## Goal

Raise Whisper transcription accuracy on short voice commands — the one-to-three-second
utterances that make up most of what people actually say to a satellite. Long dictation is
already good; the short case is where the model has the least context to work with and where
every downstream gate is hardest on it.

Production runs `Whisper-Large-v3-Turbo` (`ggml-large-v3-turbo.bin`) on whisper.cpp under
Lemonade, Vulkan on the iGPU. Nothing here changes the model.

## Evidence

Measured against the production Lemonade at `192.168.5.45:13305` on 2026-08-01, with clips
synthesized by the same box's Kokoro TTS and downsampled to the 16 kHz mono s16le the satellites
send. These are TTS clips, not mic captures, so they demonstrate failure *classes* rather than
field error rates.

- **A per-request `prompt` is accepted and it works.** The same 2.9 s clip decoded at
  `avg_logprob −0.123` with no prompt and `−0.052` with a matching one, and the output style
  followed the prompt. Lemonade forwards the field to whisper-server. The hub has never sent it.
- **Splitting an utterance costs accuracy.** `"Pon el temporizador de 10 minutos en la cocina."`
  decoded whole at `−0.043`. Cut in two, the halves decoded as `"Pone el temporizador de 10."`
  (`−0.623`) and `"10 minutos en la cocina."` — wrong verb, duplicated number. The cut was
  mid-word, so the segmenting gate (which cuts at silence) does better than this; the loss of
  cross-fragment context is the part that carries over.
- **The container's initial prompt leaks into output.** A short padded clip transcribed as
  `" P. ej."`; the same audio with an empty prompt gave `" Paras."`. The configured
  `STT_INITIAL_PROMPT` ends with `p. ej. Valladolid.`.
- **Short clips score systematically worse.** 2.9 s → `−0.12`; 0.75 s → `−0.23`, and `−0.61`
  under an unhelpful prompt. The gibberish gate's floor is a single duration-blind constant.
- **There is no cross-request context carryover.** The same file decoded bit-stable before and
  after an unrelated file, so whisper-server's `--no-context` is not needed.

## 1. Per-request prompt

`OpenAiSpeechToText` posts `file`, `model`, `response_format` and `language`. It gains `prompt`.

New on `OpenAiSttConfig`:

- `Prompt` (string?, default a Castilian assistant-command prompt in `appsettings.json`)
- `MaxPromptChars` (int, default 700)

`Prompt` is per-satellite overridable through `OpenAiSttOverrides` and a
`SatelliteConfig.ResolvePrompt(string? global)`, the same shape `Language`,
`AvgLogProbThreshold` and `NoSpeechProbThreshold` already use. Rooms differ, so the unit in the
kitchen can bias differently from the one in the office.

`TranscriptionOptions` gains two fields to carry this down the decorator chain: `Locality` (for
the placeholder below) and `Prompt` (the prior-segment text of section 2). `OpenAiSpeechToText`
is where the builder runs — it is the only component holding both the configured static prompt
and the per-call options.

### Placeholders

`{room}` and `{locality}` are substituted from the capture's satellite. `TranscriptionOptions`
already carries `Room`; `Locality` is new, and `TranscriptionOptionsFactory` fills both from
`SatelliteConfig`. An absent value substitutes empty and the result has its whitespace
collapsed, so a satellite with no `Locality` does not leave a hole or a dangling comma. An
unrecognized `{placeholder}` is left literally alone — it is far likelier to be Spanish text
than a typo'd variable, and silently deleting it would be worse than printing it.

### Composition

A new `WhisperPromptBuilder` (pure, in `Services/Stt/`) composes:

```
<static prompt>  <prior segment text>
```

Whisper treats the prompt as text preceding the audio, so the continuation text must sit last,
closest to what is being decoded. whisper.cpp caps the prompt at `n_text_ctx / 2` (224 tokens)
and keeps the tail, which would silently eat the static vocabulary. So the builder caps at
`MaxPromptChars` itself and trims **the prior text from its front**, at a word boundary: the
static vocabulary always survives whole and the most recent context is what gets dropped first.
`MaxPromptChars` is a character approximation of a token budget; 700 is deliberately under the
real limit rather than tuned to it.

Either half may be absent. Both absent means no `prompt` field is sent at all, which is exactly
today's behaviour.

## 2. Segmentation

`SegmentedSpeechToText` slices an utterance at 350 ms pauses once a segment holds 800 ms of
speech, and decodes each slice as an independent POST. Two changes, both on
`SegmentedSttConfig`.

**`ChainContext`** (bool, default true). Each segment's decode awaits the previous segment's
result and passes its text through `TranscriptionOptions.Prompt`, so a fragment is decoded as
the continuation it actually is. This is whisper's own mechanism for exactly this problem.

Chaining serializes decodes by construction. `MaxInFlightDecodes` is already 1, so nothing is
lost today, but the two settings now interact: with `ChainContext` on, `MaxInFlightDecodes > 1`
buys no parallelism. That combination logs a warning at construction rather than silently doing
one thing while the config says another.

**`FirstSplitAfterMs`** (int, default 4000). The segmenting gate's `EndUtterance` is ignored
until that much audio has been captured, so an utterance shorter than the threshold is always
decoded whole. Only the *first* split is gated; once an utterance has proven itself long, later
splits behave as they do now and keep the overlap-with-speech latency win. The existing
`MinSegmentMs` already stops later segments from being tiny.

Ignoring a decision does not disturb the gate: if speech resumes, the trailing-silence run
resets and no split is pending; if silence continues, the next chunk past the threshold splits.

## 3. Initial prompt

`DockerCompose/lemonade/entrypoint.sh`'s `INITIAL_PROMPT` default drops the `(castellano)`
parenthetical and the `p. ej. Valladolid` tail, both of which are meta-language about the domain
rather than the kind of sentence we want out of the model, and one of which was observed being
emitted verbatim.

The container default stays, as the fallback for callers that are not the hub — the eval harness
posts to the same endpoint. The hub-side `Stt.OpenAi.Prompt` is authoritative for hub traffic,
because a per-request `prompt` replaces the server's `--prompt` for that request.

## 4. whisper-server flags

Four new knobs in `entrypoint.sh`, appended to `whispercpp.args` on the existing
`${VAR-default}` convention (unset inherits the tuned default, set-but-empty disables the flag):

| Env | Default | Flag | Why |
|---|---|---|---|
| `STT_SUPPRESS_NST` | `1` | `--suppress-nst` | Suppresses non-speech tokens; the round-1 eval recorded `[Música]`-class and YouTube-boilerplate output on low-content audio |
| `STT_VAD_SPEECH_PAD_MS` | `150` | `--vad-speech-pad-ms` | whisper.cpp pads VAD segments by 30 ms, tight enough to clip a leading plosive off a one-word command |
| `STT_VAD_MIN_SPEECH_MS` | `150` | `--vad-min-speech-duration-ms` | The 250 ms default can discard a word like "sí" or "para" outright |
| `STT_BEST_OF` | `5` | `--best-of` | whisper.cpp defaults to 2 against OpenAI's 5; applies on temperature fallback |

The two VAD flags are only emitted inside the branch that already established VAD is active
(threshold set *and* the model file present), so a VAD-less boot cannot pass VAD arguments.

The four matching pass-through entries go into `docker-compose.yml` next to
`STT_VAD_THRESHOLD` / `STT_INITIAL_PROMPT` / `STT_BEAM_SIZE`. These are container-side decode
knobs, which is why they are env and not `appsettings.json`: the hub never reads them.

`--no-context` is deliberately **not** added — measured unnecessary, see Evidence.

## 5. Duration-aware quality gate

`TranscriptDispatcher` drops a transcript whose duration-weighted `avg_logprob` falls under a
single constant (`−1.0`). Short phrases score lower for reasons that have nothing to do with
being wrong, so a correct short command is likelier to be dropped than a correct long one.

New on `OpenAiSttConfig`, mirroring the `SpeakerVerification` pair that already solves this
exact problem for embeddings:

- `ShortSpeechAvgLogProbThreshold` (double, default `−1.4`)
- `FullThresholdSpeechMs` (int, default `2000`)

Below `FullThresholdSpeechMs` of measured speech the looser floor applies; at or above it, the
existing floor. Both are per-satellite overridable through the same `Resolve*` pattern.
`TranscriptDispatcher` selects on `CaptureStats.SpeechMs`, which it already receives; null stats
keep today's behaviour exactly.

`NoSpeechProbThreshold` is left alone. It measured near zero on every short clip, so there is no
evidence it is the thing dropping anything.

## Eval harness

`scripts/stt-enhancement-eval/` already scores WER over a corpus with a `lemonade` backend. Two
additions.

**Decode-config A/B.** `lemonade_worker.py` gains `--prompt`, forwarded as a multipart field,
plumbed through `backends.py` and the transcribe stage, and recorded in the run artifacts so the
report can label which decode config produced a column. That makes the prompt (and, by
restarting the container, the flags of section 4) measurable on the existing corpus of real
household speech.

**Short-command corpus.** A `SHORT_COMMANDS` phrase list plus a corpus builder that synthesizes
each phrase through the running Lemonade's Kokoro TTS and resamples to 16 kHz mono s16le.

This corpus is **synthetic speech**, and that limit is load-bearing: it has no room, no
reverberation, no far-field mic, and no speaker variation beyond the TTS voices. It can compare
two decode configurations against each other on identical audio, which is what sections 1–5 need
from it. It cannot produce a WER that transfers to the deployed satellite, and any result
written from it must say so — the same way `results/2026-07-round1.md` carries its synthetic
mixing caveat. Real short-command recordings remain the only way to get a transferable number.

## Testing

Red-Green-Refactor throughout, unit tests in `Tests/Unit/McpChannelVoice/`.

- `WhisperPromptBuilder`: placeholder substitution, missing values, unknown placeholders,
  composition order, front-trim of prior text at a word boundary, both-halves-absent.
- `OpenAiSpeechToText`: the `prompt` form field present when set and absent when not — the
  existing tests already capture the multipart form structurally.
- `SegmentedSpeechToText`: no split before `FirstSplitAfterMs`; splits normally after it; each
  segment's options carry the prior segment's text; `ChainContext` off restores today's shape.
- `SatelliteConfig`: `ResolvePrompt` and the two new threshold resolvers fall back to the global.
- `TranscriptDispatcher`: a short low-scoring transcript that passes the loose floor dispatches;
  the same score at full duration drops; null stats use the full floor.
- `VoiceSettingsBindingTests` / `AgentAppSettingsTests`-style binding coverage for every new
  setting, and `LemonadeEntrypointConfigTests` (integration, `STT_CONFIG_ONLY=1`) for the four
  new flags and the cleaned prompt default.

## Out of scope

- Changing the model. `Whisper-Large-v3` proper is a one-line `STT_MODEL` change and can be
  measured with the harness afterwards.
- Post-STT lexicon correction against Home Assistant entity names. It needs entity data the
  voice channel does not have, and it is a larger design than these five.
- Pulling recent conversation turns into the prompt. Considered and dropped: it puts
  conversation state in the STT path and risks priming the model to repeat itself.
