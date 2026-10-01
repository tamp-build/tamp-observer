using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tamp.Observer.Domain;
using Tamp.Observer.Evaluator;
using Tamp.Observer.Storage.Postgres;

// Runnable evaluator (ADR 0004): polls the spool the Go collector lands into and promotes entities
// into the Postgres/Marten store. Floor config via env vars; no cloud anything.

var connectionString = Environment.GetEnvironmentVariable("OBSERVER_DB")
    ?? "Host=localhost;Port=5432;Database=observer;Username=observer;Password=observer";
var spoolDirectory = Environment.GetEnvironmentVariable("OBSERVER_SPOOL")
    ?? "./_spool";

// Admin one-shot: create the trust-root Project (ADR 0007: a human creates projects; they are never
// auto-created from telemetry). Usage: create-project <key> [name]
if (args.Length >= 2 && args[0] == "create-project")
{
    var key = args[1];
    var name = args.Length >= 3 ? args[2] : key;
    using var store = ObserverStore.For(connectionString);
    await using var admin = store.LightweightSession();
    admin.Store(new Project { Key = key, Name = name, CreatedAtUtc = DateTimeOffset.UtcNow });
    await admin.SaveChangesAsync();
    Console.WriteLine($"created project '{key}' ({name})");
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTampObserverStore(connectionString);
builder.Services.AddSingleton(new SpoolReader(spoolDirectory));
builder.Services.AddSingleton<IngestEvaluator>();
builder.Services.AddHostedService<SpoolIngestWorker>();

builder.Build().Run();
