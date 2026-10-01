using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tamp.Observer.Domain;
using Tamp.Observer.Evaluator;
using Tamp.Observer.RawBucket.Abstractions;
using Tamp.Observer.RawBucket.FileSpool;
using Tamp.Observer.RawBucket.Valkey;
using Tamp.Observer.Storage.Postgres;

// Runnable evaluator (ADR 0004): drains the raw bucket the Go collector lands into and promotes
// entities into the Postgres/Marten store. Floor config via env vars; no cloud anything.

var connectionString = Environment.GetEnvironmentVariable("OBSERVER_DB")
    ?? "Host=localhost;Port=5432;Database=observer;Username=observer;Password=observer";
var spoolDirectory = Environment.GetEnvironmentVariable("OBSERVER_SPOOL")
    ?? "./_spool";
// Raw-bucket tier dial (ADR 0004 section 2): "file" (floor) or "valkey" (high).
var rawBucketTier = Environment.GetEnvironmentVariable("OBSERVER_RAWBUCKET") ?? "file";
var valkeyConnection = Environment.GetEnvironmentVariable("OBSERVER_VALKEY") ?? "localhost:6379";

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
builder.Services.AddSingleton<IRawBucketReader>(_ => rawBucketTier switch
{
    "valkey" => new ValkeyRawBucketReader(valkeyConnection),
    _ => new FileSpoolRawBucketReader(spoolDirectory),
});
builder.Services.AddSingleton<IngestEvaluator>();
builder.Services.AddHostedService<SpoolIngestWorker>();

await builder.Build().RunAsync();
