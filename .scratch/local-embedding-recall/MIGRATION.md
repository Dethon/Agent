# Migration runbook — moving the memory index to 1024 dimensions

The code change and the data change cannot be verified apart. The new build refuses to start
against an index of the old width by design, and a rebuilt index breaks the old build's
recall. So the index is rebuilt live, on the running old build, and the new build is deployed
after it.

No memory is ever deleted. The index is dropped without `DD`, so every `memory:<user>:<id>`
hash survives. A failed run is recovered by running the script again, not by restoring a
backup.

## Before you start

The Lemonade container must already be serving the embedding model. That arrives with the new
image, so build and push it first, and restart Lemonade alone:

```
docker compose build lemonade
docker compose up -d lemonade
curl -s http://localhost:13305/api/v1/health | python3 -m json.tool | grep -A3 pinned_models
```

`pinned_models.embedding` must be at least 1 before going further. If it is 0 the entrypoint's
pull is still running or failed; check `docker compose logs lemonade`.

## 1. Dry run

```
docker run --rm --network jackbot_default \
    -v "$PWD/migrate-embeddings.py:/migrate.py:ro" \
    -e DRY_RUN=1 \
    python:3.12-slim sh -c "pip -q install redis==5.* requests && python /migrate.py"
```

It reports how many memories it found and embeds each one without writing anything. If the
model returns a width other than 1024 it stops before touching the index.

## 2. Rebuild

Same command without `DRY_RUN`. It drops the index, rewrites every vector, and recreates the
index at 1024.

From this moment the old build's recall queries fail. They are swallowed by the recall hook's
catch-all, so turns keep working without a recall block. That is the whole exposure of the
window.

## 3. Deploy

```
docker compose build agent
docker compose up -d agent
```

The new build's startup check compares its configured dimension against the live index. If it
starts, they agree. If it refuses, the message names both values and step 2 did not finish.

## 4. Re-run the rebuild

Run step 2 again.

This is not belt and braces. During the window between step 2 and step 3 the old build was
still running, and its extraction worker can have written a vector at the old width — a memory
the new build would never be able to search, silently. The second run catches any stray. It is
idempotent: re-embedding a memory that is already correct rewrites it with the same value.

## Verifying

```
docker compose exec redis redis-cli FT.INFO idx:memories | grep -A2 dim
docker compose exec redis redis-cli --scan --pattern 'memory:*:*' | wc -l
```

The dimension must read 1024, and the memory count must match what step 1 reported. Then say
something to the agent that a stored memory should answer, and check the recall block came
back — `metrics:memory-recall:<date>` records the memory count per turn.
