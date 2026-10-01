using Marten;

namespace Tamp.Observer.Storage.Postgres;

/// <summary>
/// Convenience factory for a standalone <see cref="IDocumentStore"/> outside a DI container (tests,
/// tools, one-off spikes). Production hosting uses <see cref="ServiceCollectionExtensions.AddTampObserverStore"/>.
/// </summary>
public static class ObserverStore
{
    /// <summary>Build a configured observer document store for the given connection string.</summary>
    public static IDocumentStore For(string connectionString) =>
        DocumentStore.For(options => ObserverStoreConfiguration.Configure(options, connectionString));
}
