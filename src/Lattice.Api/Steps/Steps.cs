namespace Lattice.Api;

/// <summary>
/// One thing a builder does in the game. The core says what must happen; the builder decides who, when, with what
/// and how it looks. A later minor may add cases; a builder skips and reports one it does not know.
/// </summary>
public abstract record Step
{
    private Step() { }

    /// <summary>Places a kit as a piece.</summary>
    /// <param name="Kit">The kit.</param>
    /// <param name="Option">Which of the kit's pieces.</param>
    /// <param name="Uses">The kit items it uses.</param>
    /// <param name="At">Where, in which turn.</param>
    public sealed record PlaceKit(KitKey Kit, KitOption Option, ItemQuantity Uses, Placement At) : Step;

    /// <summary>One build step on a structure.</summary>
    /// <param name="On">The structure: one standing now, or the one an earlier step places.</param>
    /// <param name="Tool">The tool in hand.</param>
    /// <param name="Material">The material used, if any.</param>
    public sealed record Advance(StepTarget On, ItemKey Tool, Option<ItemQuantity> Material) : Step;

    /// <summary>One take-apart step on a structure standing now.</summary>
    /// <param name="On">The structure.</param>
    /// <param name="Tool">The tool in hand.</param>
    /// <param name="Returns">What comes back.</param>
    public sealed record TakeBack(StructureRef On, ItemKey Tool, ValueList<ItemQuantity> Returns) : Step;

    /// <summary>
    /// Merges a kit into a structure standing now so it becomes another piece (a straight becoming a T, for example).
    /// The kit stack is in the active hand and the tool in the other; the act uses up <paramref name="Extra"/> from that
    /// stack and the rest stays in hand. Later steps name the piece it becomes by <see cref="StepTarget.PlacedBy"/> this
    /// step. Held added in contract 0.20.
    /// </summary>
    /// <param name="Into">The structure.</param>
    /// <param name="Becomes">The piece it becomes.</param>
    /// <param name="Held">
    /// The kit stack in the active hand when the act starts: the new piece's kit item, as many as placing that piece
    /// uses (at least 1), even when the merge uses up none of it.
    /// </param>
    /// <param name="OffhandTool">The tool in the other hand.</param>
    /// <param name="Extra">The kit items the act uses up from the held stack, if any; none when the merge is free.</param>
    public sealed record Merge(StructureRef Into, PrefabKey Becomes, ItemQuantity Held, ItemKey OffhandTool, Option<ItemQuantity> Extra) : Step;

    /// <summary>
    /// Paints a structure with a spray can of the colour in the active hand; the act uses up part of the can. Can and
    /// ActsPerCan added in contract 0.20.
    /// </summary>
    /// <param name="On">The structure: one standing now, or the one an earlier step places.</param>
    /// <param name="Colour">The colour.</param>
    /// <param name="Can">The spray can item that sprays the colour.</param>
    /// <param name="ActsPerCan">
    /// How many paint acts a full can gives: each act takes the same share of the can, as the game spends it, so one
    /// act uses up 1/ActsPerCan of a full can. <see cref="int.MaxValue"/> for a can that never empties.
    /// </param>
    public sealed record Paint(StepTarget On, PaintColour Colour, ItemKey Can, int ActsPerCan) : Step;

    /// <summary>
    /// Names a structure with a labeller in the active hand, switched on and charged; nothing is used up. Labeller added
    /// in contract 0.20.
    /// </summary>
    /// <param name="On">The structure: one standing now, or the one an earlier step places.</param>
    /// <param name="To">The name.</param>
    /// <param name="Labeller">The labeller item.</param>
    public sealed record Rename(StepTarget On, ThingName To, ItemKey Labeller) : Step;

    /// <summary>
    /// Sets a device's labeller value with a labeller in the active hand, switched on and charged; nothing is used up.
    /// Labeller added in contract 0.20.
    /// </summary>
    /// <param name="On">The structure: one standing now, or the one an earlier step places.</param>
    /// <param name="To">The setting.</param>
    /// <param name="Labeller">The labeller item.</param>
    public sealed record SetValue(StepTarget On, Setting To, ItemKey Labeller) : Step;
}

/// <summary>
/// The structure a step acts on. A later minor may add cases; a builder skips and reports one it does not know.
/// </summary>
public abstract record StepTarget
{
    private StepTarget() { }

    /// <summary>A structure standing in the world now.</summary>
    /// <param name="Structure">The structure.</param>
    public sealed record Standing(StructureRef Structure) : StepTarget;

    /// <summary>
    /// The structure an earlier step of the same <see cref="StepPlan"/> places: a <see cref="Step.PlaceKit"/> or a
    /// <see cref="Step.Merge"/>; the builder knows its id once that step is done.
    /// </summary>
    /// <param name="Step">The index of that step in <see cref="StepPlan.Steps"/>.</param>
    public sealed record PlacedBy(int Step) : StepTarget;
}

/// <summary>A condition on carrying out steps. A later minor may add cases; a builder skips and reports one it does not know.</summary>
public abstract record Constraint
{
    private Constraint() { }

    /// <summary>At most one join of pipe networks per game tick.</summary>
    public sealed record OnePipeJoinPerTick : Constraint;

    /// <summary>The network must be off while these steps run.</summary>
    /// <param name="Network">The network.</param>
    public sealed record NetworkOff(NetworkRef Network) : Constraint;

    /// <summary>Only after another ghost is done.</summary>
    /// <param name="Ghost">The ghost.</param>
    public sealed record After(GhostId Ghost) : Constraint;
}

/// <summary>The exact steps from the world as it is to one ghost's target, with their constraints.</summary>
/// <param name="Ghost">The ghost.</param>
/// <param name="Steps">The steps, in order.</param>
/// <param name="Constraints">The conditions on carrying them out.</param>
public sealed record StepPlan(GhostId Ghost, ValueList<Step> Steps, ValueList<Constraint> Constraints);

/// <summary>
/// Materials and tools for a set of ghosts. A builder has <see cref="Needed"/> plus <see cref="Held"/> of each item; the
/// steps use up only <see cref="Needed"/>.
/// </summary>
/// <param name="Needed">Items used.</param>
/// <param name="GivenBack">Items returned by take-apart steps.</param>
/// <param name="Net">Needed minus given back, per item.</param>
/// <param name="Tools">Tools used.</param>
public sealed record Bill(
    ValueList<ItemQuantity> Needed,
    ValueList<ItemQuantity> GivenBack,
    ValueList<ItemDelta> Net,
    ValueSet<ItemKey> Tools)
{
    /// <summary>
    /// Items a step holds in hand and gets back, on top of <see cref="Needed"/>: per item, the most any one step holds
    /// beyond what it uses up (a <see cref="Step.Merge"/>'s <see cref="Step.Merge.Held"/> minus its
    /// <see cref="Step.Merge.Extra"/>), the largest over the steps, not their sum, as each comes back before the next.
    /// Empty when no step holds anything back. Added in contract 0.20.
    /// </summary>
    public ValueList<ItemQuantity> Held { get; init; } = ValueList<ItemQuantity>.Empty;
}

/// <summary>
/// Steps for one ghost. Main thread. Lattice implements this; consumers call it. A minor may add members: to fake it in
/// a test, inherit <c>Lattice.Testing.Stubs.StubSteps</c>, which gains every added member in the same release.
/// </summary>
public interface ISteps
{
    /// <summary>
    /// The steps from the world as it is now; refused when the ghost is unknown or cannot be built. A step on a structure
    /// an earlier step places names it <see cref="StepTarget.PlacedBy"/> that step. Cost: the ghost's steps.
    /// </summary>
    Result<StepPlan, Reason> For(GhostId ghost);
}

/// <summary>
/// Bills for sets of ghosts. Main thread. Lattice implements this; consumers call it. A minor may add members: to fake
/// it in a test, inherit <c>Lattice.Testing.Stubs.StubBills</c>, which gains every added member in the same release.
/// </summary>
public interface IBills
{
    /// <summary>The bill for the ghosts a selector picks; refused for an unknown selector kind. Cost: linear in the ghosts, cached per revision.</summary>
    Result<Bill, Reason> For(Selector which);
}
