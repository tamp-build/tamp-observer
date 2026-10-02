# Bundled dev OIDC provider image (ADR 0013): the stock Dex with our dev config baked in, so there is no
# host bind mount (more robust on Docker Desktop and fully self-contained). Build context is the repo root.
FROM dexidp/dex:v2.41.1
COPY deploy/dex/config.yaml /etc/dex/config.yaml
ENTRYPOINT ["dex", "serve", "/etc/dex/config.yaml"]
