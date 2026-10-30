namespace Lattice.Api;

/// <summary>What a builder builds. A later minor may add cases; refused <see cref="ReasonCodes.UnknownKind"/>. Added in contract 0.22.</summary>
public abstract record BuildScope
{
    private BuildScope() { }

    /// <summary>Any piece.</summary>
    public sealed record Any : BuildScope;

    /// <summary>The pieces these kits place.</summary>
    /// <param name="Of">The kits.</param>
    public sealed record Kits(ValueSet<KitKey> Of) : BuildScope;

    /// <summary>The pieces of these families.</summary>
    /// <param name="Of">The families.</param>
    public sealed record Families(ValueSet<PieceFamily> Of) : BuildScope;
}

/// <summary>Where a builder works. A later minor may add cases; refused <see cref="ReasonCodes.UnknownKind"/>. Added in contract 0.22.</summary>
public abstract record BuilderArea
{
    private BuilderArea() { }

    /// <summary>
    /// Spheres on one centre. A ghost is in build reach when the straight-line distance from <paramref name="At"/> to
    /// its point (<c>Placement.At.ToWorld()</c>, the point its <c>PlaceKit</c> puts it at) is at most
    /// <paramref name="BuildReach"/>, inclusive. A source is in range on the same rule, to the source's position. The
    /// area is where the builder works, not where its body is.
    /// </summary>
    /// <param name="At">The centre, in world metres.</param>
    /// <param name="BuildReach">How far from the centre it builds.</param>
    /// <param name="SourceRange">How far from the centre it takes items; none for a builder that carries its own stock.</param>
    public sealed record Sphere(WorldPoint At, Metres BuildReach, Option<Metres> SourceRange) : BuilderArea;
}

/// <summary>A builder's word on itself, beside what Lattice counts from its claims. Added in contract 0.22.</summary>
public abstract record BuilderWord
{
    private BuilderWord() { }

    /// <summary>Nothing to say: Lattice tells idle from working by its claims.</summary>
    public sealed record Ready : BuilderWord;

    /// <summary>It cannot go on (off, no power, needs items, nothing in reach).</summary>
    /// <param name="Why">The reason, as it puts it on the ghosts it waits on.</param>
    public sealed record Stuck(Reason Why) : BuilderWord;
}

/// <summary>
/// What a builder can supply. <see cref="Reaches"/>: all it holds or reaches, for this builder alone, never summed
/// across builders. <see cref="Toward"/>: exclusive: what this builder has set aside for each plan it names, counted by
/// no other builder and toward no other plan. Lattice carries it and cannot verify it. Added in contract 0.22.
/// </summary>
/// <param name="Reaches">Everything it holds or can reach now. At most 256 items.</param>
/// <param name="Toward">What it has set aside, per plan. At most 64 plans and 256 items a plan.</param>
public sealed record SupplyReport(
    ValueList<ItemQuantity> Reaches,
    ValueMap<PlanId, ValueList<ItemQuantity>> Toward);

/// <summary>A builder's answer to a plan assigned to it. Added in contract 0.22.</summary>
public abstract record AssignmentAnswer
{
    private AssignmentAnswer() { }

    /// <summary>It takes the plan on.</summary>
    public sealed record Accepted : AssignmentAnswer;

    /// <summary>It does not.</summary>
    /// <param name="Why">The reason (<see cref="ReasonCodes.OutOfReach"/>, <see cref="ReasonCodes.NoPower"/>, <see cref="ReasonCodes.NotFound"/>, or its own).</param>
    public sealed record Refused(Reason Why) : AssignmentAnswer;
}

/// <summary>How a builder is doing: from its claims first, then its word. Added in contract 0.22.</summary>
public abstract record BuilderState
{
    private BuilderState() { }

    /// <summary>No working claim, and its word is <see cref="BuilderWord.Ready"/>.</summary>
    public sealed record Idle : BuilderState;

    /// <summary>It holds a claim it reported working, or has not reported on yet; this beats a stuck word.</summary>
    public sealed record Working : BuilderState;

    /// <summary>No working claim, and its word is <see cref="BuilderWord.Stuck"/>.</summary>
    /// <param name="Why">Its reason.</param>
    public sealed record Stuck(Reason Why) : BuilderState;
}

/// <summary>A supply report and the game time Lattice took it. Added in contract 0.22.</summary>
/// <param name="Report">The report.</param>
/// <param name="At">When Lattice took it.</param>
public sealed record Supply(SupplyReport Report, GameTime At);

/// <summary>A registered builder as every seat sees it. Added in contract 0.22.</summary>
/// <param name="Info">Who it is, fixed at registration.</param>
/// <param name="Area">Where it works; none: nowhere in particular.</param>
/// <param name="State">How it is doing.</param>
/// <param name="Supply">Its latest supply report; none before it offers one.</param>
public sealed record Builder(
    ExecutorInfo Info,
    Option<BuilderArea> Area,
    BuilderState State,
    Option<Supply> Supply);

/// <summary>One builder assigned to a plan, and where that stands. Added in contract 0.22.</summary>
public abstract record Assignment
{
    private Assignment() { }

    /// <summary>Registered, and it has not answered yet.</summary>
    /// <param name="By">The builder.</param>
    public sealed record Asked(ExecutorId By) : Assignment;

    /// <summary>It took the plan on.</summary>
    /// <param name="By">The builder.</param>
    public sealed record Accepted(ExecutorId By) : Assignment;

    /// <summary>It refused it.</summary>
    /// <param name="By">The builder.</param>
    /// <param name="Why">Its reason.</param>
    public sealed record Refused(ExecutorId By, Reason Why) : Assignment;

    /// <summary>Not registered in this world now; the assignment stays until someone unassigns it.</summary>
    /// <param name="By">The builder.</param>
    public sealed record Absent(ExecutorId By) : Assignment;
}

/// <summary>One builder's progress on one plan, counted by Lattice. Added in contract 0.22.</summary>
/// <param name="Built">Ghosts of the plan completed while this builder held their claim.</param>
/// <param name="Working">Its live claims in the plan last reported working, or not reported on.</param>
/// <param name="Waiting">Its live claims in the plan last reported waiting or paused.</param>
public readonly record struct BuilderProgress(int Built, int Working, int Waiting);
