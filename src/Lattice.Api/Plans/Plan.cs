using System;

namespace Lattice.Api;

/// <summary>
/// A plan: every ghost belongs to exactly one. Higher-layer data (workflow flags such as draft or approved) is stored
/// and synced but never acted on by the core.
/// </summary>
/// <param name="Id">Its id, never reused.</param>
/// <param name="Revision">The board revision of its last change.</param>
/// <param name="Name">Its name.</param>
/// <param name="Owner">The actor who owns it.</param>
/// <param name="MadeBy">What made it.</param>
/// <param name="Visibility">Who may see it, when a policy enforces it.</param>
/// <param name="HigherLayerData">Data of higher layers under their own keys.</param>
public sealed record Plan(
    PlanId Id,
    Revision Revision,
    PlanName Name,
    Actor Owner,
    Maker MadeBy,
    Visibility Visibility,
    ValueMap<AttachmentKey, Attachment> HigherLayerData)
{
    /// <summary>
    /// The plan's colour, for any reader to tint by; kept as written and never interpreted. None: no colour, each reader
    /// picks its own. Added in contract 0.22.
    /// </summary>
    public Option<Tint> Colour { get; init; }

    /// <summary>
    /// The builders asked to build it, a set: many at once, at most 64. Saved and synced with the plan; it does not gate
    /// claims. Added in contract 0.22.
    /// </summary>
    public ValueSet<ExecutorId> BuildBy { get; init; } = ValueSet<ExecutorId>.Empty;
}

/// <summary>A new plan to create. The submitting actor becomes its owner.</summary>
/// <param name="Name">Its name.</param>
/// <param name="MadeBy">What makes it.</param>
/// <param name="Visibility">Who may see it.</param>
/// <param name="HigherLayerData">Data of higher layers under their own keys.</param>
public sealed record PlanDraft(
    PlanName Name,
    Maker MadeBy,
    Visibility Visibility,
    ValueMap<AttachmentKey, Attachment> HigherLayerData)
{
    /// <summary>The new plan's colour; none: no colour. Added in contract 0.22.</summary>
    public Option<Tint> Colour { get; init; }
}

/// <summary>One edit to a plan. A later minor may add cases; refused <see cref="ReasonCodes.UnknownKind"/>.</summary>
public abstract record PlanEdit
{
    private PlanEdit() { }

    /// <summary>A new name.</summary>
    /// <param name="To">The name.</param>
    public sealed record Rename(PlanName To) : PlanEdit;

    /// <summary>A new owner.</summary>
    /// <param name="To">The owner.</param>
    public sealed record ChangeOwner(Actor To) : PlanEdit;

    /// <summary>A new visibility.</summary>
    /// <param name="To">The visibility.</param>
    public sealed record ChangeVisibility(Visibility To) : PlanEdit;

    /// <summary>Sets higher-layer data under a key.</summary>
    /// <param name="Key">The key.</param>
    /// <param name="Value">The data.</param>
    public sealed record SetData(AttachmentKey Key, Attachment Value) : PlanEdit;

    /// <summary>Removes higher-layer data under a key.</summary>
    /// <param name="Key">The key.</param>
    public sealed record ClearData(AttachmentKey Key) : PlanEdit;

    /// <summary>A new colour; none clears it. Added in contract 0.22.</summary>
    /// <param name="To">The colour.</param>
    public sealed record Recolour(Option<Tint> To) : PlanEdit;

    /// <summary>
    /// Adds a builder to the plan's set; the others stay as they are. Refused <see cref="ReasonCodes.NotFound"/> (problem
    /// <c>executor</c>) when it is not registered on the host as the edit is applied, <see cref="ReasonCodes.TooLarge"/>
    /// past 64; a builder already in the set changes nothing. Added in contract 0.22.
    /// </summary>
    /// <param name="Builder">The builder.</param>
    public sealed record Assign(ExecutorId Builder) : PlanEdit;

    /// <summary>Drops a builder from the plan's set; it lets its claims there go itself. Added in contract 0.22.</summary>
    /// <param name="Builder">The builder.</param>
    public sealed record Unassign(ExecutorId Builder) : PlanEdit;
}

/// <summary>A finished plan as the finished-plans index lists it, without opening its file.</summary>
/// <param name="Id">The plan.</param>
/// <param name="Name">Its name.</param>
/// <param name="Owner">Its owner.</param>
/// <param name="FinishedAt">When it was finished, real time.</param>
/// <param name="Pieces">How many pieces it built.</param>
/// <param name="Min">One corner of the box around it.</param>
/// <param name="Max">The opposite corner.</param>
public sealed record FinishedPlan(
    PlanId Id,
    PlanName Name,
    Actor Owner,
    DateTimeOffset FinishedAt,
    int Pieces,
    Cell Min,
    Cell Max);

/// <summary>A plan in local coordinates (the origin is cell 0,0,0). Placing it makes ordinary, unlinked ghosts.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Ghosts">The ghosts, in local coordinates; their plan fields are ignored when placed.</param>
public sealed record Template(TemplateName Name, ValueList<GhostDraft> Ghosts);
