# Ziggurat

An AI agent reachable over several transports, with its own tools, memory and
observability. This file is the glossary: what each term means here, and which
near-synonyms not to use for it. It holds no implementation detail.

## Observability

**Metrics publisher**:
The fire-and-forget thing a caller holds to record a metric. Publishing through it
cannot fail, cannot block and cannot be observed.
_Avoid_: metrics client, metrics writer, telemetry publisher

**Metric sink**:
The transport a metrics publisher drains into. Sending through a sink is a real
network operation that may fail.
_Avoid_: metrics backend, metrics transport, metrics exporter
