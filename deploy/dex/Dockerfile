# Bundled dev OIDC provider image (ADR 0013): the stock Dex with our dev config baked in, so there is no
# host bind mount (more robust on Docker Desktop and fully self-contained). Build context is the repo root.
FROM dexidp/dex:v2.41.1
COPY deploy/dex/config.yaml /etc/dex/config.yaml
# Stated rather than inherited (docker:S6471). Upstream dex already runs as 1001; making it explicit
# means a future base-image change cannot silently promote this to root.
USER 1001
ENTRYPOINT ["dex", "serve", "/etc/dex/config.yaml"]
