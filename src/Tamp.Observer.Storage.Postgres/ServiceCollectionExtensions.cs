using Marten;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<IObservabilityStore, MartenObservabilityStore>();
        return services;
    }
}
