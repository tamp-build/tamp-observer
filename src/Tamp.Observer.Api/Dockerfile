# The single-host image (ADR 0001/0014): one container serves both the API and the built Svelte SPA. Build
# context is the repo root. Stage 1 builds the SPA from the committed typed client (no .NET needed); stage 2
# publishes the .NET API; the runtime stage serves the SPA from wwwroot.

FROM node:22-alpine AS frontend
WORKDIR /frontend
COPY frontend/package.json frontend/package-lock.json ./
# --ignore-scripts: nothing in the SPA's dependency tree needs a lifecycle script to build, and a
# postinstall hook in a transitive package is arbitrary code execution inside the image build
# (docker:S6505). Verified by building this stage.
RUN npm ci --ignore-scripts
COPY frontend/ ./
# Uses the committed schema.d.ts (openapi.json is already in the context); no live backend needed here.
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ ./src/
# Skip build-time OpenAPI doc generation: it writes into the source tree (frontend/) which is not part of this
# stage, and the committed spec/client are what the SPA was built from anyway.
RUN dotnet publish src/Tamp.Observer.Api/Tamp.Observer.Api.csproj -c Release -o /app/publish \
    -p:OpenApiGenerateDocumentsOnBuild=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=dotnet /app/publish ./
COPY --from=frontend /frontend/dist ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080
# Pin HOME so DuckDB's extension cache is the same path at build time and run time.
ENV HOME=/app
# Pre-bundle the DuckDB postgres extension (ADR 0005) so the first analytical query needs no network egress:
# selftest-store runs INSTALL postgres (downloads the version-matched extension into $HOME/.duckdb) then LOAD;
# the ATTACH to a dummy database fails by design, hence `|| true`. At run time INSTALL finds it cached.
RUN OBSERVER_STORE=duckdb OBSERVER_DB="Host=localhost;Port=5432;Database=x;Username=x;Password=x" \
    dotnet Tamp.Observer.Api.dll selftest-store || true
# Drop to the image's non-root app user (docker:S6471). This comes AFTER the selftest RUN above so
# the DuckDB extension cache is populated while still root, then handed over -- the app only needs
# to read it at run time, and chown keeps it writable if a future INSTALL wants to refresh it.
RUN chown -R $APP_UID:$APP_UID /app
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Tamp.Observer.Api.dll"]
