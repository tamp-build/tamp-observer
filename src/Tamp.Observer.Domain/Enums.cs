namespace Tamp.Observer.Domain;

/// <summary>
/// Storage tier for a Project (ADR 0005). Ordered low to high; migrations go up only and a downgrade
/// is illegal in the config model. The floor (<see cref="Postgres"/>) runs on nothing but Postgres.
/// </summary>
public enum StorageTier
{
    /// <summary>Postgres + Marten baseline / system of record. The floor and the default.</summary>
    Postgres = 0,

    /// <summary>DuckDB as an on-demand analytical accelerator; Postgres stays the system of record.</summary>
    DuckDbOnDemand = 1,

    /// <summary>ClickHouse opt-in top tier: an always-hot columnar ingest target for high volume.</summary>
    ClickHouse = 2,
}

/// <summary>
/// Enforcement posture (ADR 0002), mirroring tamp.findings' vocabulary. Absence of a per-project value
/// inherits the instance default; a locked instance turns its mode into a non-weakenable floor.
/// </summary>
public enum EnforcementMode
{
    /// <summary>Friendly, permissive, fully-configurable default. Nothing is blocked on policy grounds.</summary>
    Advisory = 0,

    /// <summary>Locked-down posture; enforcement-surface loosenings are refused.</summary>
    Enforcing = 1,
}
