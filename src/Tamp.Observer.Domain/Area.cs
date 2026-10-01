namespace Tamp.Observer.Domain;

/// <summary>
/// A project-scoped metadata tag for filter/group/report only (ADR 0007). No hierarchy, no behavior,
/// no children, no access control. Events are many-to-many with Areas. A lean managed vocabulary (a
/// thin per-project lookup) rather than free text, so reports stay coherent and renames are clean.
/// </summary>
public class Area
{
    /// <summary>Marten document identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning Project.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>The tag name (unique within the Project's managed vocabulary).</summary>
    public required string Name { get; set; }
}
