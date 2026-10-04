using Marten;
using Microsoft.Extensions.DependencyInjection;
using Tamp.Observer.Domain;
using Tamp.Observer.Storage.Abstractions;

namespace Tamp.Observer.Storage.Postgres;

/// <summary>
/// DI registration for the Postgres baseline store. The floor install wires this and nothing else
/// (ADR 0001): .NET + Postgres, no Valkey, no ClickHouse.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the observer Marten store (Postgres baseline tier) against the given connection string.
    /// </summary>
    public static IServiceCollection AddTampObserverStore(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMarten(options => ObserverStoreConfiguration.Configure(options, connectionString));

        // The Postgres translators for the two ADR 0006 seams: the write sink and the read interface.
        services.AddSingleton<IEventSink, MartenEventSink>();
        // The read store needs the raw connection string too: the TOBS-25 rollup tables are plain SQL (not Marten
        // documents), read via Npgsql.
        services.AddSingleton<IObservabilityStore>(sp => new MartenObservabilityStore(
            sp.GetRequiredService<IDocumentStore>(), connectionString));

        // The authorization chokepoint (ADR 0013).
        services.AddSingleton<IAuthorizationService, MartenAuthorizationService>();

        // The admission list (ADR 0013): who is pre-registered to use the instance at all.
        services.AddSingleton<IAllowedIdentityStore, MartenAllowedIdentityStore>();

        // Issue read/write (ADR 0015); Postgres-canonical, not a per-engine translator.
        services.AddSingleton<IIssueStore, MartenIssueStore>();

        // Symbol artifact store (ADR 0017); the symbolicator itself lives in the Symbolication assembly.
        services.AddSingleton<ISymbolArtifactStore, MartenSymbolArtifactStore>();

        // Replay session metadata store (ADR 0010); the blob store is a filesystem/object-storage concern wired
        // by the host, like the raw bucket.
        services.AddSingleton<IReplaySessionStore, MartenReplaySessionStore>();
        return services;
    }
}
