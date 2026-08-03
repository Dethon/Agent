# 04 — CaptureSession and the coordinator rewrite

**What to build:** The turn-taking loop takes fourteen injected members, eleven of them mandatory, each carrying an ordering contract in prose, wired by an eighty-four line object initialiser inside the connection handler. Four of those members are one idea — the microphone capture: open it, close it and read its statistics at exactly that moment, tell the satellite the user stopped talking, tell the satellite the mic is live again.

After this ticket a capture module owns that idea for a connection. Opening returns the capture the loop needs. Closing returns the frozen gate statistics and, at that same instant, records the room sample and the speech-end anchor — the statistics must be frozen at the close, because the endpointing tail is what anchors speech end and it must not be re-read later. The module asks the gate factory for its gates, so opening a capture without recording its room sample is no longer something a new call site can do by omission.

The loop drops to about ten injected members, five of them mandatory: the capture module and the turn replace six between them. The three metric side-effects and the two early-verification members stay. The plan's estimate of roughly six members assumed the metric callbacks folded too; they do not, because folding them drags the metrics publisher into the module, which is separate work and out of scope here.

The eighty-four line initialiser becomes wiring for two modules and the callbacks that genuinely belong to the loop.

**Blocked by:** 01 — Per-satellite gate factory. 03 — VoiceTurn, the segment token, and the reply-tool collapse.

**Status:** done

- [x] One module owns the capture lifecycle for a connection: open, close, speech-stopped, listening-started.
- [x] Closing freezes the gate statistics at the close and records the room sample and the speech-end anchor at that instant.
- [x] The module obtains its gates from the factory; no gate is assembled inline anywhere in the connection path.
- [x] The loop's injected surface drops to about ten members, five mandatory, with the capture module and the turn replacing six.
- [x] The loop's existing unit file passes: its coverage of every loop exit — abandoned, no-speech, undispatched, reply timeout, max turns — is the regression net for this rewrite.
- [x] A failure writing either satellite indicator event still never costs the user the utterance or the window it was announcing; both are indicator-only by contract.
- [x] The integration files get the minimal edit needed to stay green; restructuring them is ticket 05.
- [x] The comments recording why speech-stopped and listening-started are best-effort, and why the speech-end anchor is rewound by the frozen tail, move with the code.
