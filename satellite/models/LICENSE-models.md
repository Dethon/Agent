# Model licenses

The three ONNX models in this directory are **CC-BY-NC-SA 4.0 (NonCommercial)** — fine for
this personal home satellite, **not redistributable in a commercial product**. The
openWakeWord *code* is Apache-2.0; the license below covers the *pretrained models* only.

| File | Source | License |
|---|---|---|
| `melspectrogram.onnx` | [dscripka/openWakeWord v0.5.1](https://github.com/dscripka/openWakeWord/releases/tag/v0.5.1) | CC-BY-NC-SA 4.0 |
| `embedding_model.onnx` | [dscripka/openWakeWord v0.5.1](https://github.com/dscripka/openWakeWord/releases/tag/v0.5.1) | CC-BY-NC-SA 4.0 |
| `ok_nabu.onnx` | [fwartner/home-assistant-wakewords-collection](https://github.com/fwartner/home-assistant-wakewords-collection/blob/main/en/ok_nabu/ok_nabu.onnx) (`en/ok_nabu`, 205,647 bytes) | CC-BY-NC-SA 4.0 |

The melspectrogram/embedding models derive from Google's
[speech_embedding](https://tfhub.dev/google/speech_embedding/1); the `ok_nabu` classifier is a
community-trained openWakeWord head for the "ok nabu" wake phrase.
