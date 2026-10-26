namespace Lattice.Api;

/// <summary>
/// A change to the board, as data: it can be sent, written to a plan file and previewed.
/// Primitive commands are applied by the board itself: <see cref="Place"/>, <see cref="Remove"/>, <see cref="Change"/>,
/// <see cref="Move"/>, <see cref="CreatePlan"/>, <see cref="EditPlan"/>, <see cref="MoveToPlan"/>, <see cref="Revert"/>,
/// <see cref="FinishPlan"/>, <see cref="CatchUp"/> and a <see cref="Batch"/> of them. Every other command is
/// expanded into primitive ones by the module that owns it, through a registered expander; with no expander it is
/// refused with <see cref="ReasonCodes.ModuleMissing"/>. A later minor may add cases; an unknown case is refused
/// with <see cref="ReasonCodes.UnknownKind"/>. Every command that makes ghosts names their maker: in each draft
/// (<see cref="GhostDraft.MadeBy"/>), in its request (<see cref="RunRequest.MadeBy"/>,
/// <see cref="RouteRequest.MadeBy"/>), as its last parameter <c>MadeBy</c>, or for copies in
/// <see cref="TransformMode.Copy"/>; Lattice tells that maker when the ghosts are blocked.
/// </summary>
public abstract record Command
{
    private Command() { }

    /// <summary>Places new ghosts. A ghost in a spot that already holds one replaces it.</summary>
    /// <param name="Ghosts">The ghosts to place.</param>
    public sealed record Place(ValueList<GhostDraft> Ghosts) : Command;

    /// <summary>Removes the ghosts a selector picks.</summary>
    /// <param name="Which">The ghosts.</param>
    public sealed record Remove(Selector Which) : Command;

    /// <summary>Edits the ghosts a selector picks; refused if any changed after <paramref name="UnchangedSince"/>.</summary>
    /// <param name="Which">The ghosts.</param>
    /// <param name="Edit">The edit.</param>
    /// <param name="UnchangedSince">The board revision the sender saw; refused if any ghost changed after it. Without it the edit overwrites whatever changed since.</param>
    public sealed record Change(Selector Which, GhostEdit Edit, Option<Revision> UnchangedSince) : Command;

    /// <summary>
    /// Moves build ghosts, keeping their ids: every ghost in <paramref name="To"/> moves at once to its placement, so
    /// ghosts moved onto each other's old spots never replace each other. A ghost outside the moved set in a spot one
    /// lands on is replaced, as <see cref="Place"/> does. Refused <see cref="ReasonCodes.NotFound"/> (naming the
    /// ghosts) when a ghost is not on the board or the actor may not see it, <see cref="ReasonCodes.InvalidValue"/> for
    /// an empty map, a ghost that is not a build ghost or two moved ghosts landing in one spot, and like a placement
    /// for an unknown piece or a small-cell clash.
    /// </summary>
    /// <param name="To">Each moved ghost's new placement, by id.</param>
    /// <param name="UnchangedSince">The board revision the sender saw; refused if any moved ghost changed after it. Without it the move overwrites whatever changed since.</param>
    public sealed record Move(ValueMap<GhostId, Placement> To, Option<Revision> UnchangedSince) : Command;

    /// <summary>Creates a plan owned by the submitting actor, with <see cref="Ghosts"/> placed in it.</summary>
    /// <param name="Draft">The new plan.</param>
    public sealed record CreatePlan(PlanDraft Draft) : Command
    {
        /// <summary>
        /// Ghosts placed in the new plan as part of the same change, as <see cref="Place"/> would place them (a ghost
        /// in a spot that holds one replaces it); their plan fields are ignored. Empty by default: the plan starts empty.
        /// </summary>
        public ValueList<GhostDraft> Ghosts { get; init; } = ValueList<GhostDraft>.Empty;
    }

    /// <summary>Edits a plan: name, owner, visibility, higher-layer data.</summary>
    /// <param name="Plan">The plan.</param>
    /// <param name="Edit">The edit.</param>
    /// <param name="UnchangedSince">The board revision the sender saw; refused if the plan changed after it. Without it the edit overwrites whatever changed since.</param>
    public sealed record EditPlan(PlanId Plan, PlanEdit Edit, Option<Revision> UnchangedSince) : Command;

    /// <summary>Moves the ghosts a selector picks into another plan.</summary>
    /// <param name="Which">The ghosts.</param>
    /// <param name="To">The plan.</param>
    public sealed record MoveToPlan(Selector Which, PlanId To) : Command;

    /// <summary>Undoes a change set of the same actor; refused if another actor changed those ghosts since. Plan changes only, never built work.</summary>
    /// <param name="ChangeSet">The change set to undo.</param>
    public sealed record Revert(ChangeSetId ChangeSet) : Command;

    /// <summary>
    /// Applies all the steps or none, as one change set. Each step is applied to the board as the earlier steps leave
    /// it. From contract 0.8 an expanded step (a run, a route, a template, a swap) is also expanded against the board
    /// as the earlier steps leave it, so what it lays meets their ghosts as it would meet ghosts already placed: a
    /// branch run after its trunk joins it with a tee. A step worked out over several frames holds back the steps after
    /// it; the batch is answered once every step is found.
    /// </summary>
    /// <param name="Steps">The commands, in order.</param>
    public sealed record Batch(ValueList<Command> Steps) : Command;

    /// <summary>Finishes a plan: it leaves the active list and is kept as a record.</summary>
    /// <param name="Plan">The plan.</param>
    public sealed record FinishPlan(PlanId Plan) : Command;

    /// <summary>After a crash: moves a plan forward to the changes its file holds beyond the loaded save.</summary>
    /// <param name="Plan">The plan.</param>
    public sealed record CatchUp(PlanId Plan) : Command;

    /// <summary>Marks the structures a selector picks for removal (remove ghosts). Expanded by the selectors module.</summary>
    /// <param name="Which">The structures.</param>
    /// <param name="Into">The plan, or the default plan.</param>
    /// <param name="MadeBy">What makes the remove ghosts.</param>
    public sealed record MarkRemoval(StructureSelector Which, Option<PlanId> Into, Maker MadeBy) : Command;

    /// <summary>"Swap all": swap ghosts for every structure a selector picks. Expanded by the swap planner.</summary>
    /// <param name="Which">The structures.</param>
    /// <param name="To">What each becomes.</param>
    /// <param name="Into">The plan, or the default plan.</param>
    /// <param name="MadeBy">What makes the swap ghosts.</param>
    public sealed record PlanSwap(StructureSelector Which, SwapTo To, Option<PlanId> Into, Maker MadeBy) : Command;

    /// <summary>Finish ghosts for every structure a selector picks. Expanded by the selectors module.</summary>
    /// <param name="Which">The structures.</param>
    /// <param name="Finish">The finish.</param>
    /// <param name="Into">The plan, or the default plan.</param>
    /// <param name="MadeBy">What makes the finish ghosts.</param>
    public sealed record PlanFinish(StructureSelector Which, Finish Finish, Option<PlanId> Into, Maker MadeBy) : Command;

    /// <summary>A drawn run of cable, pipe or chute. Expanded by the run planner.</summary>
    /// <param name="Request">The run.</param>
    public sealed record PlanRun(RunRequest Request) : Command;

    /// <summary>A run found between two ends. Expanded by the route planner.</summary>
    /// <param name="Request">The route.</param>
    public sealed record PlanRoute(RouteRequest Request) : Command;

    /// <summary>
    /// Moves, turns or mirrors ghosts (expanded into one <see cref="Move"/>), or places transformed copies. Expanded by
    /// the transforms module.
    /// </summary>
    /// <param name="Which">The ghosts.</param>
    /// <param name="How">The transformation.</param>
    /// <param name="Mode">Move them, or place transformed copies made by the copy's maker.</param>
    public sealed record Transform(Selector Which, Transformation How, TransformMode Mode) : Command;

    /// <summary>Places a template as a new plan of ordinary, unlinked ghosts. Expanded by the templates module.</summary>
    /// <param name="Template">The template.</param>
    /// <param name="Origin">The cell its origin lands on.</param>
    /// <param name="Turn">Its turn about the origin.</param>
    /// <param name="As">The new plan.</param>
    public sealed record PlaceTemplate(Template Template, Cell Origin, Turn Turn, PlanDraft As) : Command;

    /// <summary>
    /// Places a template's ghosts into an existing plan, or the default plan, as ordinary unlinked ghosts: one change
    /// set that opens no plan, so <see cref="Revert"/> undoes it like a <see cref="Place"/>. The drafts keep their looks,
    /// maker, groups and attached data; their plan fields are ignored. Refused as <see cref="PlaceTemplate"/> (fact
    /// <c>drafts</c>), and <see cref="ReasonCodes.NotFound"/> (fact <c>plan</c>) for a plan the actor may not see or the
    /// board does not hold. A ghost in a spot that already holds one replaces it, as <see cref="Place"/>. Expanded by
    /// the templates module.
    /// Added in contract 0.10.
    /// </summary>
    /// <param name="Template">The template.</param>
    /// <param name="Origin">The cell its origin lands on.</param>
    /// <param name="Turn">Its turn about the origin.</param>
    /// <param name="Into">The plan, or the default plan.</param>
    public sealed record PlaceTemplateIn(Template Template, Cell Origin, Turn Turn, Option<PlanId> Into) : Command;

    /// <summary>Ghosts for whatever a finished plan built that has gone or changed since. Expanded by the finished-plans module.</summary>
    /// <param name="Plan">The finished plan.</param>
    /// <param name="MadeBy">What makes the repair plan and its ghosts.</param>
    public sealed record Repair(PlanId Plan, Maker MadeBy) : Command;

    /// <summary>Remove ghosts for everything a finished plan built that still stands. Expanded by the finished-plans module.</summary>
    /// <param name="Plan">The finished plan.</param>
    /// <param name="MadeBy">What makes the take-down plan and its ghosts.</param>
    public sealed record TakeDown(PlanId Plan, Maker MadeBy) : Command;

    /// <summary>
    /// Makes a finished plan active again, history intact; the ghosts it gives back keep their ids and makers, so it
    /// makes no ghosts of its own. Applied by the board once saving is installed.
    /// </summary>
    /// <param name="Plan">The finished plan.</param>
    public sealed record Reopen(PlanId Plan) : Command;

    /// <summary>
    /// Plans the long straights among the targets as single pieces. A ghost long straight is replaced at once by
    /// single-piece ghosts, which keep its plan, groups, label, tint and attachments. A built one is planned as a run
    /// joining it plans it: a removal ghost for it and a build ghost for each of its cells. Pieces that are not long
    /// straights are left alone. Refused <see cref="ReasonCodes.InvalidValue"/> (<c>problem=nothing-to-split</c>) when
    /// no target is one. Expanded by the runs module. Added in contract 0.9.
    /// </summary>
    /// <param name="Which">The ghosts or structures to split.</param>
    /// <param name="Into">The plan for a built piece's ghosts, or the default plan.</param>
    /// <param name="MadeBy">What makes the new ghosts.</param>
    public sealed record PlanSplit(SplitTargets Which, Option<PlanId> Into, Maker MadeBy) : Command;
}

/// <summary>What a split acts on. Union: a later minor may add cases. Added in contract 0.9.</summary>
public abstract record SplitTargets
{
    private SplitTargets() { }

    /// <summary>Ghosts: the build ghosts among them whose piece is a long straight.</summary>
    /// <param name="Which">The ghosts.</param>
    public sealed record Ghosts(Selector Which) : SplitTargets;

    /// <summary>Built structures: the long straights among them that no ghost removes or swaps.</summary>
    /// <param name="Which">The structures.</param>
    public sealed record Structures(StructureSelector Which) : SplitTargets;
}

/// <summary>A new ghost to place. The board gives it an id, revision, placer, time and state.</summary>
/// <param name="Intent">What it asks for.</param>
/// <param name="Plan">The plan, or the default plan.</param>
/// <param name="MadeBy">What makes it.</param>
/// <param name="Visibility">Who may see it.</param>
/// <param name="Label">A label, if any.</param>
/// <param name="Tint">A colour, if not the default.</param>
/// <param name="Groups">Its groups.</param>
/// <param name="Attached">Mods' data under their own keys.</param>
public sealed record GhostDraft(
    Intent Intent,
    Option<PlanId> Plan,
    Maker MadeBy,
    Visibility Visibility,
    Option<Label> Label,
    Option<Tint> Tint,
    ValueSet<GroupName> Groups,
    ValueMap<AttachmentKey, Attachment> Attached);

/// <summary>One edit to a ghost.</summary>
public abstract record GhostEdit
{
    private GhostEdit() { }

    /// <summary>Sets or clears the label.</summary>
    /// <param name="To">The label, or none.</param>
    public sealed record SetLabel(Option<Label> To) : GhostEdit;

    /// <summary>Sets or clears the tint.</summary>
    /// <param name="To">The tint, or none.</param>
    public sealed record SetTint(Option<Tint> To) : GhostEdit;

    /// <summary>Adds the ghost to a group.</summary>
    /// <param name="Group">The group.</param>
    public sealed record JoinGroup(GroupName Group) : GhostEdit;

    /// <summary>Takes the ghost out of a group.</summary>
    /// <param name="Group">The group.</param>
    public sealed record LeaveGroup(GroupName Group) : GhostEdit;

    /// <summary>Sets or clears the finish of a build or swap ghost, or replaces a finish ghost's finish.</summary>
    /// <param name="To">The finish, or none (refused on a finish ghost).</param>
    public sealed record SetFinish(Option<Finish> To) : GhostEdit;

    /// <summary>Sets who may see the ghost.</summary>
    /// <param name="To">The visibility.</param>
    public sealed record SetVisibility(Visibility To) : GhostEdit;

    /// <summary>Attaches data under a key, replacing what was there.</summary>
    /// <param name="Key">The key.</param>
    /// <param name="Value">The data.</param>
    public sealed record Attach(AttachmentKey Key, Attachment Value) : GhostEdit;

    /// <summary>Removes the data under a key.</summary>
    /// <param name="Key">The key.</param>
    public sealed record Detach(AttachmentKey Key) : GhostEdit;
}

/// <summary>Whether a transform moves ghosts or places transformed copies. A later minor may add cases; an unknown case is refused with <see cref="ReasonCodes.UnknownKind"/>.</summary>
public abstract record TransformMode
{
    private TransformMode() { }

    /// <summary>The ghosts themselves move, ids and makers kept.</summary>
    public sealed record Move : TransformMode;

    /// <summary>Transformed copies are placed; the originals stay.</summary>
    /// <param name="MadeBy">What makes the copies.</param>
    public sealed record Copy(Maker MadeBy) : TransformMode;
}

/// <summary>A move, turn or mirror of ghosts.</summary>
public abstract record Transformation
{
    private Transformation() { }

    /// <summary>Moves by an offset.</summary>
    /// <param name="By">The offset.</param>
    public sealed record Move(CellOffset By) : Transformation;

    /// <summary>Turns about a cell.</summary>
    /// <param name="Pivot">The cell turned about.</param>
    /// <param name="Turn">The turn.</param>
    public sealed record Rotate(Cell Pivot, Turn Turn) : Transformation;

    /// <summary>Mirrors across a plane; pieces with no mirror image are reported, not placed.</summary>
    /// <param name="Plane">The plane.</param>
    public sealed record Mirror(MirrorPlane Plane) : Transformation;
}

/// <summary>What a swapped structure becomes.</summary>
public abstract record SwapTo
{
    private SwapTo() { }

    /// <summary>This piece.</summary>
    /// <param name="Piece">The piece.</param>
    public sealed record Exactly(PrefabKey Piece) : SwapTo;

    /// <summary>The piece of the same shape that this kit places (every cable shape to its heavy cable shape, for example).</summary>
    /// <param name="Kit">The kit.</param>
    public sealed record SameShapeFrom(KitKey Kit) : SwapTo;
}

/// <summary>A run of cable, pipe or chute drawn through cells, with bends at the waypoints.</summary>
/// <param name="Kit">The kit the run is laid from.</param>
/// <param name="From">The first cell.</param>
/// <param name="Via">Bend cells, in order.</param>
/// <param name="To">The last cell.</param>
/// <param name="Into">The plan, or the default plan.</param>
/// <param name="MadeBy">What makes the run's ghosts.</param>
public sealed record RunRequest(KitKey Kit, Cell From, ValueList<Cell> Via, Cell To, Option<PlanId> Into, Maker MadeBy)
{
    /// <summary>
    /// The port the run starts at. When set, <see cref="From"/> must be that port's <see cref="Port.Joins"/> as the
    /// sender read it (refused <see cref="ReasonCodes.ChangedSince"/> when the port has moved since), and the run's
    /// first piece leaves toward the port, so it joins it. Added in contract 0.8.
    /// </summary>
    public Option<PortEnd> FromPort { get; init; }

    /// <summary>The port the run ends at, as <see cref="FromPort"/> with <see cref="To"/>. Added in contract 0.8.</summary>
    public Option<PortEnd> ToPort { get; init; }

    /// <summary>How the run's pieces are chosen. Added in contract 0.9.</summary>
    public LayOptions Lay { get; init; } = LayOptions.Default;
}

/// <summary>How a run's or route's pieces are chosen. Added in contract 0.9.</summary>
/// <param name="SinglePiecesOnly">Never a long straight: every cell its own piece.</param>
/// <param name="MatchGrade">
/// Lay with the kit of the run the new run joins, when that kit lays the same network kinds (a heavy cable off a heavy
/// main): the first end that joins a run decides, the from end before the to end.
/// </param>
public sealed record LayOptions(bool SinglePiecesOnly, bool MatchGrade)
{
    /// <summary>Long straights where they fit, and the request's own kit: what a 0.8 request does.</summary>
    public static LayOptions Default { get; } = new(false, false);
}

/// <summary>A run found between two ends under the given toggles.</summary>
/// <param name="Kit">The kit the run is laid from.</param>
/// <param name="From">One end.</param>
/// <param name="To">The other end.</param>
/// <param name="Toggles">What the search must respect.</param>
/// <param name="Into">The plan, or the default plan.</param>
/// <param name="MadeBy">What makes the run's ghosts.</param>
public sealed record RouteRequest(KitKey Kit, Cell From, Cell To, RouteToggles Toggles, Option<PlanId> Into, Maker MadeBy)
{
    /// <summary>
    /// The most search states each search (each leg of the main route, each branch) may take, at least 1; none lets
    /// Lattice scale the bound with the distance between that search's ends. A search that reaches it is refused
    /// <see cref="ReasonCodes.SearchLimit"/>; a bound below 1 is refused <see cref="ReasonCodes.InvalidValue"/> (fact
    /// <c>problem</c> = <c>search-bound</c>).
    /// </summary>
    public Option<int> SearchBound { get; init; }

    /// <summary>
    /// The port the route starts at, as <see cref="RunRequest.FromPort"/> with <see cref="From"/>. With
    /// <see cref="RouteToggles.KeepPortsFree"/>, the port's own <see cref="Port.Joins"/> cell is open to the route.
    /// Added in contract 0.8.
    /// </summary>
    public Option<PortEnd> FromPort { get; init; }

    /// <summary>The port the route ends at, as <see cref="FromPort"/> with <see cref="To"/>. Added in contract 0.8.</summary>
    public Option<PortEnd> ToPort { get; init; }

    /// <summary>How the route's pieces are chosen. Added in contract 0.9.</summary>
    public LayOptions Lay { get; init; } = LayOptions.Default;

    /// <summary>
    /// Extra starts, each routed in order to the nearest cell of the run so far (the main route and the branches
    /// before it), where it joins with a T. A branch stops at the first run cell it reaches, so no loop is made. At
    /// most <c>MaxBranches</c> (16); more is refused <see cref="ReasonCodes.TooLarge"/> (fact <c>branches</c>). A branch
    /// with no route refuses the command as a main route that fails does, with fact <c>branch</c> (its index).
    /// Added in contract 0.9.
    /// </summary>
    public ValueList<RouteBranch> Branches { get; init; } = ValueList<RouteBranch>.Empty;

    /// <summary>
    /// Cells the route passes through, in order: the route is searched leg by leg (from the start to the first, ...,
    /// from the last to the end), each leg the cheapest between its two cells under the toggles, and laid as one run.
    /// A later leg never enters an earlier leg's cells, so the route never crosses itself. An earlier leg is not
    /// searched again to make room for a later one. Unlike <see cref="RunRequest.Via"/>, these are not bends: the
    /// route may run straight through. At most <c>MaxVia</c> (16); more is refused <see cref="ReasonCodes.TooLarge"/>
    /// (fact <c>via</c>). A cell off the kit's grid is refused <see cref="ReasonCodes.InvalidValue"/> (<c>off-grid</c>,
    /// fact <c>via</c>, its index), and one equal to an end or to another of them is refused (<c>via-repeats</c>). A leg
    /// with no route refuses the command with the search's reason and fact <c>leg</c>: 0 from the start, the last
    /// from the last via cell to the end. Added in contract 0.12.
    /// </summary>
    public ValueList<Cell> Via { get; init; } = ValueList<Cell>.Empty;
}

/// <summary>One extra start of a route; <paramref name="Port"/> as <see cref="RouteRequest.FromPort"/>. Added in contract 0.9.</summary>
/// <param name="From">The cell the branch starts at.</param>
/// <param name="Port">The port it starts at, if any: <paramref name="From"/> must be that port's <see cref="Port.Joins"/>.</param>
public sealed record RouteBranch(Cell From, Option<PortEnd> Port);

/// <summary>Which leg a route takes first when routes of equal cost differ only in order. Added in contract 0.9.</summary>
public enum FirstLeg
{
    /// <summary>Up or down first (the game's Y), then across.</summary>
    Vertical = 1,

    /// <summary>Across first, then up or down.</summary>
    Level = 2,
}

/// <summary>What a route search must respect. Each toggle is independent.</summary>
/// <param name="OnFrames">Only through cells with frames (built or planned).</param>
/// <param name="FewestBends">Prefer fewer bends over a shorter run.</param>
/// <param name="KeepDoorsAndWindowsClear">Never in front of a door or window.</param>
/// <param name="KeepPortsFree">Never across a device port.</param>
/// <param name="NoBridging">Never join two networks (what <see cref="ReasonCodes.WouldBridge"/> reports).</param>
public sealed record RouteToggles(
    bool OnFrames,
    bool FewestBends,
    bool KeepDoorsAndWindowsClear,
    bool KeepPortsFree,
    bool NoBridging)
{
    /// <summary>
    /// Which leg first among equal-cost routes; none keeps the search's own order. Never makes a route longer or adds a
    /// bend. Added in contract 0.9.
    /// </summary>
    public Option<FirstLeg> First { get; init; }

    /// <summary>
    /// Cells beside no frame and no wall cost one more cell, so the route follows structure; a soft preference, unlike
    /// <see cref="OnFrames"/>. Added in contract 0.9.
    /// </summary>
    public bool HugFramesAndWalls { get; init; }
}
