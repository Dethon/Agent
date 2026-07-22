# TSE ONNX Separation Core + Embedding Cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run the wesep BSRNN separation core via onnxruntime CPU and cache the per-speaker ECAPA embedding in the tse-extractor sidecar, cutting extraction from ~2.6 s to ~1.6 s per 8 s capture with numerically identical output (fp32 tolerance ~1e-7 — no WER re-eval needed).

**Architecture:** A `BsrnnCore` torch wrapper module re-exposes the trained band-split → BN → separator → mask slice of `wesep.models.bsrnn.BSRNN.forward` with real/imag tensors only (the ONNX exporter rejects complex STFT). It is exported once at container startup into the `/models` volume and verified against the eager path before use; STFT/iSTFT, kaldi fbank, and the ECAPA `spk_model` stay in torch on the host. The embedding is computed once per enrollment signature and cached next to the existing concat-wav cache. Any failure at any stage falls back to the eager `extractor.extract_speech` path (fail-open, loud log).

**Tech Stack:** torch 2.1.2 CPU, onnxruntime 1.27.0 (already in image), onnx 1.22.0 (new build dep), opset 17, Flask sidecar.

## Global Constraints

- Evidence base (adversarially verified 2026-07-22, workflow wf_995392bc): ORT LSTM kernel ~1.9× faster than torch oneDNN on both dominant shapes; export via wrapper proven in-container (86 MB, dynamic time axis, max-abs-diff ≤ 1.71e-7 vs eager on 8 s and 14 s real speech); E2E ~1.63–1.69 s per 8 s capture at 12 threads on the 5900X (eager: 2.38–2.6 s).
- Deployed model config (`/models/wesep-english/config.yaml`): `joint_training: true`, `spk_feat: true`, `spk_emb_dim: 192`, `use_spk_transform: false` (spk_transform = `nn.Identity`). The runtime must assert these and fall back to eager if they ever differ.
- Output equivalence gate: max-abs-diff < 1e-5 between ONNX pipeline and `extract_speech_from_pcm` on the verification input, else the ONNX artifact is discarded and eager is used.
- The sidecar runs as root (no `user:` override on `tse-extractor` in compose) — it may write `/models`. `/voices` is `:ro`; the writable caches live in `/tmp/enroll-cache` and `/models`.
- New env vars must land in `DockerCompose/docker-compose.yml` in the same commit (repo rule). `TSE_ONNX` is non-secret → no `.env` entry.
- `torchaudio.load` works on torch/torchaudio 2.1.2 (the torchcodec breakage is in newer torchaudio — do not bump).
- HTTP contract of `/extract` and `/health` unchanged; the .NET integration suite (`Tests/Integration/McpChannelVoice/TseExtractorServiceTests.cs`) must pass 3/3 unmodified.
- Serialize `dotnet` runs (WSL RCU livelock rule). Commit on the checked-out branch (`noisev2`).

## File Structure

- Create: `DockerCompose/tse-extractor/onnx_core.py` — wrapper module, export, verify, load-or-export orchestration. Self-contained; imports wesep only via the caller-provided `Extractor`.
- Modify: `DockerCompose/tse-extractor/app.py` — embedding cache, ORT extraction pipeline, eager fallback, `TSE_ONNX` knob.
- Modify: `DockerCompose/tse-extractor/Dockerfile` — add pinned `onnx` package (separate `RUN` layer to preserve the torch layer cache).
- Modify: `DockerCompose/docker-compose.yml` — `TSE_ONNX` interpolation on `tse-extractor`.
- Modify: `docs/tse-trial-runbook.md` — latency expectations + kill-switch note.

---

### Task 1: `onnx_core.py` — wrapper, export, parity verify

**Files:**
- Create: `DockerCompose/tse-extractor/onnx_core.py`
- Modify: `DockerCompose/tse-extractor/Dockerfile` (add `RUN pip install --no-cache-dir "onnx==1.22.0"` after the existing pip layer)

**Interfaces:**
- Produces: `load_or_export(extractor, model_dir, threads) -> onnxruntime.InferenceSession | None` (None = caller must use eager), `run_core(sess, extractor, pcm_mix) -> torch.Tensor` taking a *precomputed-embedding-free* signature — see below: `run_core(sess, extractor, pcm_mix, embedding)` returns the normalized (1, N) extracted waveform, and `compute_embedding(extractor, enroll_path) -> torch.Tensor` returns the (1, 192) post-`spk_model` embedding.
- Consumes: the running `wesep.cli.extractor.Extractor` instance from `app.py` (`extractor.model` is the loaded `BSRNN`).

- [ ] **Step 1: Write `onnx_core.py`**

```python
"""ONNX export/runtime for the BSRNN separation core.

The eager path spends ~79% of its self-time in torch's oneDNN LSTM kernels
(aten::mkldnn_rnn_layer); onnxruntime's LSTM kernel runs the same shapes ~1.9x faster.
Only the band-split -> BN -> separator -> mask slice is exported (torch.onnx rejects
complex STFT); stft/istft/fbank/ECAPA stay in torch host-side. Output is fp32-tolerance
identical to eager (~1e-7), gated by verify() after every fresh export -- on any failure
the caller falls back to the eager path.
"""
import json
import logging
import os

import numpy as np
import torch
import torchaudio

log = logging.getLogger(__name__)

OPSET = 17
CORE_VERSION = 1  # bump to force re-export on pipeline changes
VERIFY_TOLERANCE = 1e-5  # measured parity is ~1e-7; margin catches real divergence only


class BsrnnCore(torch.nn.Module):
    """The exportable slice of wesep BSRNN.forward: everything between the STFT and the
    iSTFT, with the speaker embedding already computed (spk_model/ECAPA runs host-side).
    Mirrors wesep/models/bsrnn.py forward() with real/imag arithmetic only."""

    def __init__(self, model):
        super().__init__()
        self.band_width = model.band_width
        self.BN = model.BN
        self.separator = model.separator
        self.mask = model.mask
        self.spk_transform = model.spk_transform  # Identity for this checkpoint

    def forward(self, spec_real, spec_imag, embedding):
        batch_size = spec_real.shape[0]
        spec_ri = torch.stack([spec_real, spec_imag], 1)  # B, 2, F, T
        subband_spec = []
        subband_mix_real = []
        subband_mix_imag = []
        band_idx = 0
        for width in self.band_width:
            subband_spec.append(spec_ri[:, :, band_idx:band_idx + width].contiguous())
            subband_mix_real.append(spec_real[:, band_idx:band_idx + width])
            subband_mix_imag.append(spec_imag[:, band_idx:band_idx + width])
            band_idx += width

        subband_feature = []
        for i, bn_func in enumerate(self.BN):
            subband_feature.append(
                bn_func(subband_spec[i].view(batch_size, self.band_width[i] * 2, -1)))
        subband_feature = torch.stack(subband_feature, 1)  # B, nband, N, T

        spk_embedding = self.spk_transform(embedding)
        spk_embedding = spk_embedding.unsqueeze(1).unsqueeze(3)
        sep_output = self.separator(subband_feature, spk_embedding, torch.tensor(1))

        est_real = []
        est_imag = []
        for i, mask_func in enumerate(self.mask):
            this_output = mask_func(sep_output[:, i]).view(
                batch_size, 2, 2, self.band_width[i], -1)
            this_mask = this_output[:, 0] * torch.sigmoid(this_output[:, 1])
            mask_real = this_mask[:, 0]
            mask_imag = this_mask[:, 1]
            est_real.append(subband_mix_real[i] * mask_real
                            - subband_mix_imag[i] * mask_imag)
            est_imag.append(subband_mix_real[i] * mask_imag
                            + subband_mix_imag[i] * mask_real)
        return torch.cat(est_real, 1), torch.cat(est_imag, 1)


def _stft(model, pcm):
    window = torch.hann_window(model.win)
    return torch.stft(pcm, n_fft=model.win, hop_length=model.stride,
                      window=window, return_complex=True)


def compute_embedding(extractor, enroll_path):
    """Post-spk_model (1, 192) embedding from an enrollment wav — the cacheable 0.29s."""
    pcm, sr = torchaudio.load(enroll_path, normalize=True)
    if sr != extractor.resample_rate:
        pcm = torchaudio.transforms.Resample(orig_freq=sr,
                                             new_freq=extractor.resample_rate)(pcm)
    feats = extractor.compute_fbank(
        pcm.to(torch.float), sample_rate=extractor.resample_rate, cmn=True).unsqueeze(0)
    with torch.inference_mode():
        emb = extractor.model.spk_model(feats)
    return emb[-1] if isinstance(emb, tuple) else emb


def run_core(sess, extractor, pcm_mix, embedding):
    """Host stft -> ONNX core -> host istft + output norm. Mirrors
    extract_speech_from_pcm's joint_training branch with a precomputed embedding."""
    model = extractor.model
    with torch.inference_mode():
        spec = _stft(model, pcm_mix.to(torch.float))
        out = sess.run(None, {
            "spec_real": spec.real.numpy(),
            "spec_imag": spec.imag.numpy(),
            "embedding": embedding.numpy(),
        })
        est = torch.complex(torch.from_numpy(out[0]), torch.from_numpy(out[1]))
        speech = torch.istft(est, n_fft=model.win, hop_length=model.stride,
                             window=torch.hann_window(model.win),
                             length=pcm_mix.shape[1])
        return speech / abs(speech).max(dim=1, keepdim=True).values * 0.9


def _artifact_paths(model_dir):
    base = os.path.join(model_dir, f"bsrnn_core_v{CORE_VERSION}_opset{OPSET}")
    return base + ".onnx", base + ".json"


def _synthetic_pair(seconds, seed):
    g = torch.Generator().manual_seed(seed)
    n = 16000 * seconds
    t = torch.arange(n) / 16000.0
    tone = 0.3 * torch.sin(2 * torch.pi * (140 + 40 * torch.sin(2 * torch.pi * 0.5 * t)) * t)
    return (tone + 0.2 * torch.randn(n, generator=g)).clamp(-1, 1).unsqueeze(0)


def _verify(sess, extractor):
    """Full-pipeline parity vs the production eager path on synthetic audio."""
    mix = _synthetic_pair(4, 1)
    enroll = _synthetic_pair(8, 2)
    eager = extractor.extract_speech_from_pcm(mix, 16000, enroll, 16000)
    feats = extractor.compute_fbank(enroll, sample_rate=16000, cmn=True).unsqueeze(0)
    with torch.inference_mode():
        emb = extractor.model.spk_model(feats)
    emb = emb[-1] if isinstance(emb, tuple) else emb
    ours = run_core(sess, extractor, mix, emb)
    diff = (eager - ours).abs().max().item()
    log.info("onnx core parity vs eager: max-abs-diff %.3g", diff)
    return diff < VERIFY_TOLERANCE


def load_or_export(extractor, model_dir, threads):
    """Return a verified InferenceSession, or None (caller uses eager). Never raises."""
    try:
        import onnxruntime as ort

        if not (getattr(extractor, "joint_training", False)
                and getattr(extractor, "speaker_feat", False)):
            log.warning("onnx core disabled: unexpected model config "
                        "(joint_training=%s, spk_feat=%s)",
                        getattr(extractor, "joint_training", None),
                        getattr(extractor, "speaker_feat", None))
            return None

        onnx_path, meta_path = _artifact_paths(model_dir)
        expected_meta = {
            "core_version": CORE_VERSION,
            "opset": OPSET,
            "torch": torch.__version__,
            "checkpoint_mtime": os.path.getmtime(
                os.path.join(model_dir, "avg_model.pt")),
        }
        fresh = True
        if os.path.exists(onnx_path) and os.path.exists(meta_path):
            with open(meta_path) as f:
                if json.load(f) == expected_meta:
                    fresh = False

        if fresh:
            log.info("exporting bsrnn core to %s (one-time)", onnx_path)
            core = BsrnnCore(extractor.model).eval()
            spec = _stft(extractor.model, _synthetic_pair(4, 3))
            emb = compute_embedding.__wrapped__(extractor) if False else None
            feats = extractor.compute_fbank(
                _synthetic_pair(8, 4), sample_rate=16000, cmn=True).unsqueeze(0)
            with torch.inference_mode():
                emb = extractor.model.spk_model(feats)
            emb = emb[-1] if isinstance(emb, tuple) else emb
            tmp = onnx_path + ".tmp"
            torch.onnx.export(
                core, (spec.real, spec.imag, emb), tmp,
                opset_version=OPSET,
                input_names=["spec_real", "spec_imag", "embedding"],
                output_names=["est_real", "est_imag"],
                dynamic_axes={"spec_real": {2: "time"}, "spec_imag": {2: "time"},
                              "est_real": {2: "time"}, "est_imag": {2: "time"}})
            os.replace(tmp, onnx_path)

        opts = ort.SessionOptions()
        opts.intra_op_num_threads = threads
        sess = ort.InferenceSession(onnx_path, sess_options=opts,
                                    providers=["CPUExecutionProvider"])

        if fresh:
            if not _verify(sess, extractor):
                log.error("onnx core FAILED parity verification — deleting artifact, "
                          "falling back to eager extraction")
                os.remove(onnx_path)
                return None
            with open(meta_path, "w") as f:
                json.dump(expected_meta, f)

        log.info("onnx core active (%s)", os.path.basename(onnx_path))
        return sess
    except Exception:
        log.exception("onnx core unavailable — falling back to eager extraction")
        return None
```

**NOTE for the implementer:** the line `emb = compute_embedding.__wrapped__(extractor) if False else None` is a placeholder artifact in this plan text — DELETE it; the two lines after it (fbank + spk_model) are the real embedding computation for export dummies.

- [ ] **Step 2: Dockerfile — add the export dependency**

After the existing big `RUN pip install ...` block (keep it untouched for layer caching), add:

```dockerfile
# torch.onnx.export requires the onnx package at runtime (export happens at container
# startup because the checkpoint lives in the /models volume, not the image).
RUN pip install --no-cache-dir "onnx==1.22.0"
```

- [ ] **Step 3: Build the image and prove export + parity in-container (this is the failing-test step — it fails until the wrapper is right)**

```bash
cd /home/dethon/repos/agent/DockerCompose && docker build -q -t tse-extractor:latest ./tse-extractor
docker compose -f docker-compose.yml -f docker-compose.override.linux.yml up -d tse-extractor
# wait for health, then:
docker exec -i tse-extractor python3 - <<'EOF'
import sys, logging, time
logging.basicConfig(level=logging.INFO)
sys.path.insert(0, "/opt/wesep-src"); sys.path.insert(0, "/opt/tse")
from wesep.cli.extractor import load_model_local
import onnx_core, torch
ex = load_model_local("/models/wesep-english"); ex.set_device("cpu")
torch.set_num_threads(12)
sess = onnx_core.load_or_export(ex, "/models/wesep-english", 12)
assert sess is not None, "export/verify failed"
mix = onnx_core._synthetic_pair(8, 9)
feats = ex.compute_fbank(onnx_core._synthetic_pair(8, 2), sample_rate=16000, cmn=True).unsqueeze(0)
with torch.inference_mode():
    emb = ex.model.spk_model(feats)
emb = emb[-1] if isinstance(emb, tuple) else emb
onnx_core.run_core(sess, ex, mix, emb)  # warm
t0 = time.perf_counter(); onnx_core.run_core(sess, ex, mix, emb)
print(f"onnx 8s: {time.perf_counter()-t0:.2f}s")
t0 = time.perf_counter(); ex.extract_speech_from_pcm(mix, 16000, onnx_core._synthetic_pair(8,2), 16000)
print(f"eager 8s: {time.perf_counter()-t0:.2f}s")
EOF
```

Expected: parity log line with diff ~1e-7, `onnx 8s:` ≤ ~1.9 s, `eager 8s:` ~2.4–2.8 s, second run of the script skips export (meta match).

- [ ] **Step 4: Commit**

```bash
git add DockerCompose/tse-extractor/onnx_core.py DockerCompose/tse-extractor/Dockerfile
git commit -m "perf(tse): ONNX-exportable BSRNN separation core with startup parity gate"
```

---

### Task 2: `app.py` integration — embedding cache + ORT path + fallback

**Files:**
- Modify: `DockerCompose/tse-extractor/app.py`
- Modify: `DockerCompose/docker-compose.yml` (`TSE_ONNX` env on `tse-extractor`)

**Interfaces:**
- Consumes: `onnx_core.load_or_export / run_core / compute_embedding` from Task 1.
- Produces: unchanged HTTP contract (`POST /extract?speaker=X` → wav, `GET /health`).

- [ ] **Step 1: Wire startup**

In `app.py` after the thread-count block (order matters — threads must be set before export so ORT and the verify timing see the final count):

```python
import onnx_core

_onnx_enabled = os.environ.get("TSE_ONNX", "1") != "0"
ort_session = onnx_core.load_or_export(extractor, MODEL_DIR, torch.get_num_threads()) \
    if _onnx_enabled else None
```

(`app.py` already imports `torch` since the thread fix; `onnx_core` lives next to it at `/opt/tse` and the Dockerfile `COPY app.py entrypoint.sh` line must gain `onnx_core.py`.)

- [ ] **Step 2: Embedding cache keyed on the existing enrollment signature**

Extend the existing `_enrollment_wav` cache dir (per speaker under `CACHE`): next to the concat wav + sig file, store `embedding.npy` + `embedding.sig` (same signature string + `|core-v1`). New function:

```python
def _speaker_embedding(speaker):
    """(1, 192) post-spk_model embedding for the speaker, cached on the same
    (name,size,mtime) signature as the concat wav; None if unknown speaker."""
    enrollment = _enrollment_wav(speaker)
    if enrollment is None:
        return None
    speaker_dir = VOICES / speaker
    sig = _signature(speaker_dir) + "|emb-v1"
    emb_file = CACHE / speaker / "embedding.npy"
    sig_file = CACHE / speaker / "embedding.sig"
    if emb_file.exists() and sig_file.exists() and sig_file.read_text() == sig:
        return torch.from_numpy(np.load(emb_file))
    emb = onnx_core.compute_embedding(extractor, str(enrollment))
    np.save(emb_file, emb.numpy())
    sig_file.write_text(sig)
    return emb
```

- [ ] **Step 3: The extract endpoint uses ORT when available**

Replace the `with lock: speech = extractor.extract_speech(...)` body with:

```python
        with lock:
            if ort_session is not None:
                pcm_mix, sr = torchaudio.load(str(mix), normalize=True)
                embedding = _speaker_embedding(speaker)
                speech = onnx_core.run_core(ort_session, extractor, pcm_mix, embedding)
            else:
                speech = extractor.extract_speech(str(mix), str(enrollment))
```

(`import torchaudio` at top. `sr` is always 16000 — the hub sends WyomingStandard wavs; `extract_speech` never resampled either because `resample_rate == 16000`.) `_speaker_embedding` cannot return None here — the 404 guard above already proved the speaker has enrollment.

- [ ] **Step 4: compose skeleton (same commit)**

```yaml
      # 1 = run the BSRNN core via onnxruntime (parity-gated at startup; any failure
      # falls back to eager torch). 0 = force the eager path.
      TSE_ONNX: "${TSE_ONNX:-1}"
```

- [ ] **Step 5: Rebuild, run both modes in-container, verify identical output and speed**

Rebuild + `up -d`. Seed a synthetic speaker via the alpine-rw-mount trick (root-owned `volumes/voices`): generate 10× 8 s `enroll-*.wav` (stdlib script from the session's earlier benchmark), `docker run --rm -v .../voices:/v -v tmp:/t:ro alpine sh -c 'mkdir -p /v/Bench && cp /t/enroll-*.wav /v/Bench/'`. Then:

```bash
# ORT path (default):
/usr/bin/time -f "onnx: %es" curl -s -X POST "http://localhost:9098/extract?speaker=Bench" --data-binary @mix8.wav -o /tmp/out-onnx.wav
# second call must be faster still (embedding now cached) — compare the two timings
# eager path:
docker compose ... exec? no — recreate with TSE_ONNX=0:
TSE_ONNX=0 docker compose -f docker-compose.yml -f docker-compose.override.linux.yml up -d tse-extractor
/usr/bin/time -f "eager: %es" curl -s -X POST "http://localhost:9098/extract?speaker=Bench" --data-binary @mix8.wav -o /tmp/out-eager.wav
python3 - <<'EOF'
import wave, numpy as np
def rd(p):
    with wave.open(p) as w: return np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float64)
a, b = rd('/tmp/out-onnx.wav'), rd('/tmp/out-eager.wav')
print("len match:", len(a) == len(b), "max int16 diff:", np.abs(a - b).max())
EOF
```

Expected: onnx ≤ ~1.9 s warm, eager ~2.8–3.2 s, max int16 diff ≤ 1 (fp32 ~1e-7 × 32767 rounds to 0 or 1 LSB). Restore `TSE_ONNX` default and remove the Bench speaker afterwards.

- [ ] **Step 6: .NET integration suite**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TseExtractorServiceTests"
```
Expected: 3/3 (needs the sidecar up; re-seed happens inside the tests' own fixture if they do so — if they skip on empty speakers, run with Bench still present, then clean up).

- [ ] **Step 7: Commit**

```bash
git add DockerCompose/tse-extractor/app.py DockerCompose/tse-extractor/Dockerfile DockerCompose/docker-compose.yml
git commit -m "perf(tse): run BSRNN core via onnxruntime + cache speaker embeddings"
```

---

### Task 3: Runbook + docs

**Files:**
- Modify: `docs/tse-trial-runbook.md`

- [ ] **Step 1:** Update the pi5 latency paragraph: ONNX core is the default (`TSE_ONNX=1`), expected ~1.6 s per 8 s capture on a 5900X-class CPU, ~1.2–1.6 s on the HX 370, Pi 5 expected proportionally faster than the 10–25 s eager estimate (measure on device); `TSE_ONNX=0` is the fallback/kill switch; first container start after this change exports the core once (log line `exporting bsrnn core`, ~1–2 min on Pi) and self-verifies (`onnx core parity vs eager`).
- [ ] **Step 2:** Commit `docs: runbook for ONNX TSE core`.
