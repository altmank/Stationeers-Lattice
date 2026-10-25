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
    ValueMap<AttachmentKey, Attachment> HigherLayerData);

/// <summary>A new plan to create. The submitting actor becomes its owner.</summary>
/// <param name="Name">Its name.</param>
/// <param name="MadeBy">What makes it.</param>
/// <param name="Visibility">Who may see it.</param>
/// <param name="HigherLayerData">Data of higher layers under their own keys.</param>
public sealed record PlanDraft(
    PlanName Name,
    Maker MadeBy,
    Visibility Visibility,
    ValueMap<AttachmentKey, Attachment> HigherLayerData);

/// <summary>One edit to a plan.</summary>
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
