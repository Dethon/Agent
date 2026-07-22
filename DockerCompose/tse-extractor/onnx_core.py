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
import platform

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
            "onnxruntime": ort.__version__,
            "machine": platform.machine(),
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
            feats = extractor.compute_fbank(
                _synthetic_pair(8, 4), sample_rate=16000, cmn=True).unsqueeze(0)
            # torch.no_grad() (not inference_mode()): tensors created under
            # inference_mode are permanently barred from autograd tracing, and
            # torch.onnx.export traces via autograd -- an inference-mode embedding
            # here fails export with "Inference tensors cannot be saved for backward"
            # even though it is never used for backprop.
            with torch.no_grad():
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
