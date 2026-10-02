# Custom OTel Collector distribution (ADR 0003): ocb generates the distribution from our manifest, then we
# compile it static. Build context is the repo root so the local exporter modules referenced by the manifest's
# `replaces` (../rawfileexporter, ../valkeystreamexporter) are present next to the generated _dist.
FROM golang:1.26-bookworm AS build
WORKDIR /build
COPY src/collector/ ./
# Generate the distribution with ocb (pinned to the manifest's collector version), then build it.
RUN go run go.opentelemetry.io/collector/cmd/builder@v0.162.0 --config otelcol-builder.yaml
WORKDIR /build/_dist
RUN CGO_ENABLED=0 go build -trimpath -o /collector .

FROM gcr.io/distroless/static-debian12 AS final
COPY --from=build /collector /collector
COPY src/collector/config.docker.yaml /etc/observer/collector.yaml
# OTLP gRPC and HTTP front doors (ADR 0009).
EXPOSE 4317 4318
ENTRYPOINT ["/collector", "--config", "/etc/observer/collector.yaml"]
