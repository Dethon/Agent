# Floating `2` on purpose: BuildKit re-resolves FROM every build, so an upstream tag move re-runs
# the xcaddy compile with nothing changed here. Pinning the patch trades that ~60s rebuild for
# manual CVE bumps on the internet-facing TLS terminator -- deliberately not worth it.
FROM public.ecr.aws/docker/library/caddy:2-builder AS builder
RUN xcaddy build --with github.com/caddy-dns/cloudflare

FROM caddy:2
COPY --from=builder /usr/bin/caddy /usr/bin/caddy
