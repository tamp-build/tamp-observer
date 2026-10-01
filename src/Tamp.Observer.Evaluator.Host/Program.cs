using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tamp.Observer.Evaluator;
using Tamp.Observer.Storage.Postgres;

// Runnable evaluator (ADR 0004): polls the spool the Go collector lands into and promotes entities
// into the Postgres/Marten store. Floor config via env vars; no cloud anything.
var builder = Host.CreateApplicationBuilder(args);

var connectionString = Environment.GetEnvironmentVariable("OBSERVER_DB")
    ?? "Host=localhost;Port=5432;Database=observer;Username=observer;Password=observer";
var spoolDirectory = Environment.GetEnvironmentVariable("OBSERVER_SPOOL")
    ?? "./_spool";

builder.Services.AddTampObserverStore(connectionString);
builder.Services.AddSingleton(new SpoolReader(spoolDirectory));
builder.Services.AddSingleton<IngestEvaluator>();
builder.Services.AddHostedService<SpoolIngestWorker>();

builder.Build().Run();
