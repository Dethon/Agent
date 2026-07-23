"""Joins manifests with transcripts, emits WER tables, CSV, and the decision block."""
import csv
import json
from pathlib import Path

import jiwer

from .manifest import Utterance, read_manifest
from .textnorm import normalize

LOW_SNR_MAX = 5.0
HIGH_SNR_MIN = 10.0
MIN_REL_IMPROVEMENT = 0.20
MAX_CLEAN_DELTA = 0.02


def score_rows(manifest: list[Utterance], transcripts: dict[str, tuple[str, float | None]]) -> list[dict]:
    rows = []
    for u in manifest:
        hyp, score = transcripts.get(Path(u.wav).name, ("", None))
        ref_n, hyp_n = normalize(u.reference), normalize(hyp)
        rows.append({
            "id": u.id, "speaker": u.speaker, "interference": u.interference,
            "snr_db": u.snr_db,
            "wer": jiwer.wer(ref_n, hyp_n) if ref_n else 0.0,
            "cer": jiwer.cer(ref_n, hyp_n) if ref_n else 0.0,
            "score": score,
            "ref": u.reference, "hyp": hyp,
        })
    return rows


def _mean(xs: list[float]) -> float:
    return sum(xs) / len(xs) if xs else float("nan")


def decision(raw_cells: list[dict], model_cells: list[dict]) -> dict:
    def low_snr(cells):
        return [c["wer"] for c in cells
                if c["interference"] == "speech" and c["snr_db"] is not None and c["snr_db"] <= LOW_SNR_MAX]

    def clean(cells):
        return [c["wer"] for c in cells if c["interference"] == "none"]

    # The no-regression gate covers the clean AND high-SNR cells (spec: "does not regress the
    # clean/high-SNR cells") -- a model that helps at 0 dB but hurts the easy +15/+10 dB turns
    # would otherwise slip through as a false PASS.
    def high_snr(cells):
        return [c["wer"] for c in cells
                if c["interference"] != "none" and c["snr_db"] is not None and c["snr_db"] >= HIGH_SNR_MIN]

    raw_low, model_low = _mean(low_snr(raw_cells)), _mean(low_snr(model_cells))
    rel = (raw_low - model_low) / raw_low if raw_low else 0.0
    delta = _mean(clean(model_cells)) - _mean(clean(raw_cells))
    high_delta = _mean(high_snr(model_cells)) - _mean(high_snr(raw_cells))
    return {
        "rel_improvement_low_snr": rel,
        "clean_wer_delta": delta,
        "high_snr_wer_delta": high_delta,
        # `not >` (rather than `<=`) so a sweep with no high-SNR cells (delta = nan)
        # doesn't fail the gate on absence.
        "passes": rel >= MIN_REL_IMPROVEMENT and delta <= MAX_CLEAN_DELTA
                  and not high_delta > MAX_CLEAN_DELTA,
    }


def _load_transcripts(path: Path) -> dict[str, tuple[str, float | None]]:
    out = {}
    with path.open(encoding="utf-8") as f:
        for line in f:
            row = json.loads(line)
            out[Path(row["wav"]).name] = (row["text"], row.get("score"))
    return out


def run_report(run_dir: Path) -> None:
    manifest = read_manifest(run_dir / "manifest.jsonl")
    scored: dict[tuple[str, str], list[dict]] = {}
    scores_meta: dict[tuple[str, str], dict[str, float | None]] = {}
    for backend_dir in sorted((run_dir / "transcripts").iterdir()):
        for cond_file in sorted(backend_dir.glob("*.jsonl")):
            key = (backend_dir.name, cond_file.stem)
            scored[key] = score_rows(manifest, _load_transcripts(cond_file))

    lines = ["# STT enhancement eval report", ""]
    snrs = sorted({u.snr_db for u in manifest if u.snr_db is not None}, reverse=True)
    for backend in sorted({b for b, _ in scored}):
        conds = sorted({c for b, c in scored if b == backend})
        for interference in ("speech", "music"):
            lines += [f"## backend={backend} interference={interference}", "",
                      "| SNR (dB) | " + " | ".join(conds) + " |",
                      "|---" * (len(conds) + 1) + "|"]
            for snr in [None] + snrs:
                label = "clean" if snr is None else f"{snr:+.0f}"
                cells = []
                for cond in conds:
                    sel = [r["wer"] for r in scored[(backend, cond)]
                           if (r["interference"] == ("none" if snr is None else interference))
                           and r["snr_db"] == snr]
                    cells.append(f"{100 * _mean(sel):.1f}" if sel else "-")
                lines.append(f"| {label} | " + " | ".join(cells) + " |")
            lines.append("")
        if "raw" in conds:
            lines.append(f"### Decision (backend={backend}, rule: ≥{MIN_REL_IMPROVEMENT:.0%} relative WER cut at SNR ≤ {LOW_SNR_MAX:.0f} dB speech, clean and ≥{HIGH_SNR_MIN:.0f} dB deltas ≤ {MAX_CLEAN_DELTA})")
            for cond in conds:
                if cond == "raw":
                    continue
                d = decision(scored[(backend, "raw")], scored[(backend, cond)])
                lines.append(f"- **{cond}**: rel low-SNR improvement {d['rel_improvement_low_snr']:+.1%}, "
                             f"clean ΔWER {d['clean_wer_delta']:+.3f}, "
                             f"≥{HIGH_SNR_MIN:.0f} dB ΔWER {d['high_snr_wer_delta']:+.3f} → "
                             f"{'PASS' if d['passes'] else 'FAIL'}")
            conf = []
            for cond in conds:
                s = [r["score"] for r in scored[(backend, cond)]
                     if r["score"] is not None and r["interference"] == "speech"
                     and r["snr_db"] is not None and r["snr_db"] <= LOW_SNR_MAX]
                conf.append(f"{cond}={_mean(s):.2f}" if s else f"{cond}=-")
            lines.append(f"- mean confidence (speech ≤{LOW_SNR_MAX:.0f} dB): " + ", ".join(conf))
            lines.append("")
    takes_file = run_dir / "takes.jsonl"
    lines += ["## Caveats", "- References use scripted phrases; digit-vs-word mismatches (e.g. 'diez' vs '10') inflate all conditions equally.",]
    if takes_file.exists():
        excluded = [json.loads(line) for line in takes_file.open(encoding="utf-8")]
        names = [t["speaker"] + "-t" + str(t["take"]) for t in excluded if not t["included"]]
        lines.append(f"- Excluded takes (clean WER > 0.3): {names or 'none'}")
    (run_dir / "report.md").write_text("\n".join(lines), encoding="utf-8")

    with (run_dir / "per_utterance.csv").open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["id", "speaker", "interference", "snr_db", "condition", "backend", "wer", "cer", "score", "ref", "hyp"])
        for (backend, cond), rows in scored.items():
            for r in rows:
                w.writerow([r["id"], r["speaker"], r["interference"], r["snr_db"], cond, backend,
                            f"{r['wer']:.4f}", f"{r['cer']:.4f}", r["score"], r["ref"], r["hyp"]])
    print(f"wrote {run_dir / 'report.md'} and per_utterance.csv")