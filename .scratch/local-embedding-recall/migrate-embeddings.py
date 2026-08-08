#!/usr/bin/env python3
"""Re-embed every stored memory against Lemonade and rebuild the index at the new width.

Throwaway. It is not committed to the application; it lives beside the spec so the
migration is reproducible and re-runnable while this work is landing.

It never deletes a document. The index is dropped without DD, so every memory:<user>:<id>
hash survives and a failed run is recovered by running it again.

Usage (from the production host, on the compose network):

    docker run --rm --network jackbot_default \
        -v "$PWD/migrate-embeddings.py:/migrate.py:ro" \
        python:3.12-slim sh -c \
        "pip -q install redis==5.* requests && python /migrate.py"

Environment:
    REDIS_URL         default redis://redis:6379
    LEMONADE_URL      default http://lemonade:13305/api/v1
    EMBEDDING_MODEL   default Qwen3-Embedding-0.6B-GGUF
    DIMENSION         default 1024
    INDEX             default idx:memories
    DRY_RUN           set to 1 to report what would change and touch nothing
"""

import os
import struct
import sys

import redis
import requests

REDIS_URL = os.environ.get("REDIS_URL", "redis://redis:6379")
LEMONADE_URL = os.environ.get("LEMONADE_URL", "http://lemonade:13305/api/v1").rstrip("/")
MODEL = os.environ.get("EMBEDDING_MODEL", "Qwen3-Embedding-0.6B-GGUF")
DIMENSION = int(os.environ.get("DIMENSION", "1024"))
INDEX = os.environ.get("INDEX", "idx:memories")
DRY_RUN = os.environ.get("DRY_RUN") == "1"


def embed(text):
    response = requests.post(
        f"{LEMONADE_URL}/embeddings",
        json={"model": MODEL, "input": text},
        timeout=120,
    )
    response.raise_for_status()
    vector = response.json()["data"][0]["embedding"]
    if len(vector) != DIMENSION:
        raise SystemExit(
            f"{MODEL} returned {len(vector)} dimensions, expected {DIMENSION}. "
            "Fix DIMENSION or the model before touching the index."
        )
    return vector


def memory_keys(client):
    # memory:<userId>:<memoryId>. Profiles are memory:profile:<userId> and carry no vector,
    # so they are matched by the same glob and skipped by the type check below.
    return [
        key
        for key in client.scan_iter(match="memory:*:*", count=500)
        if client.type(key) == b"hash"
    ]


def main():
    client = redis.Redis.from_url(REDIS_URL)
    keys = memory_keys(client)
    print(f"{len(keys)} stored memories at {REDIS_URL}")

    vectors = {}
    for key in keys:
        content = client.hget(key, "content")
        if not content:
            print(f"  skip {key.decode()}: no content field")
            continue
        vectors[key] = embed(content.decode())
        print(f"  embedded {key.decode()}")

    if DRY_RUN:
        print(f"dry run: would rewrite {len(vectors)} vectors and rebuild {INDEX} at {DIMENSION}")
        return

    # Drop without DD: the documents stay, only the index definition goes.
    try:
        client.execute_command("FT.DROPINDEX", INDEX)
        print(f"dropped {INDEX} (documents kept)")
    except redis.ResponseError as ex:
        print(f"{INDEX} not dropped ({ex}); continuing")

    for key, vector in vectors.items():
        client.hset(key, "embedding", struct.pack(f"<{len(vector)}f", *vector))
    print(f"rewrote {len(vectors)} vectors at {DIMENSION} dimensions")

    # Same schema RedisStackMemoryStore.CreateIndexAsync builds, at the new width.
    client.execute_command(
        "FT.CREATE", INDEX, "ON", "HASH", "PREFIX", "1", "memory:", "SCHEMA",
        "userId", "TAG", "SEPARATOR", "|",
        "content", "TEXT",
        "category", "TAG", "SEPARATOR", ",",
        "tags", "TAG", "SEPARATOR", ",",
        "importance", "NUMERIC", "SORTABLE",
        "confidence", "NUMERIC",
        "createdAt", "NUMERIC", "SORTABLE",
        "lastAccessedAt", "NUMERIC", "SORTABLE",
        "accessCount", "NUMERIC",
        "embedding", "VECTOR", "HNSW", "6",
        "TYPE", "FLOAT32", "DIM", str(DIMENSION), "DISTANCE_METRIC", "COSINE",
    )
    print(f"recreated {INDEX} at {DIMENSION} dimensions")


if __name__ == "__main__":
    sys.exit(main())
