# The evaluator worker (ADR 0004): drains the shared file spool and promotes entities into Postgres. Build
# context is the repo root. No web surface, so it runs on the .NET runtime image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ ./src/
RUN dotnet publish src/Tamp.Observer.Evaluator.Host/Tamp.Observer.Evaluator.Host.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=dotnet /app/publish ./
ENTRYPOINT ["dotnet", "Tamp.Observer.Evaluator.Host.dll"]
