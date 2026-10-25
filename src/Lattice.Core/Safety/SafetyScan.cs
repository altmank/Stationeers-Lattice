using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Consequences;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.Support;

namespace Lattice.Core.Safety;

/// <summary>
/// One pass of build safety over one plan: the plan's live ghosts in id order, the built world, and the planned world
/// (every ghost on the board done, <see cref="PlannedWorld"/>; with <c>onlyThese</c>, only the given ghosts done) read
/// through one cache. Main thread, as the board is.
/// </summary>
internal sealed class SafetyScan
{
    private readonly Dictionary<PrefabKey, Option<PieceFacts>> _pieces = [];
    private HashSet<GasKind>? _oxidisers;

    public SafetyScan(IReadOnlyList<Ghost> plan, IGhostBoard board, IWorldView world, ICatalogue catalogue, IGrid grid, ISafetyFacts facts, SafetyLimits limits, bool onlyThese = false)
    {
        var ghosts = new List<Ghost>(plan.Count);
        foreach (var ghost in plan)
            if (ghost.State is not GhostState.Stale)
                ghosts.Add(ghost);
        ghosts.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        Ghosts = ghosts;
        InPlan = new HashSet<GhostId>();
        foreach (var ghost in ghosts)
            InPlan.Add(ghost.Id);
        World = world;
        Planned = new PlannedWorld(board, new ViewQuery(world), onlyThese ? InPlan : null);
        Catalogue = catalogue;
        Grid = grid;
        Facts = facts;
        Limits = limits;
        Shapes = new Shapes(catalogue, grid);
        Models = new WorldModels(world, catalogue, grid);
    }

    /// <summary>The plan's ghosts that are not stale, in id order.</summary>
    public IReadOnlyList<Ghost> Ghosts { get; }

    public HashSet<GhostId> InPlan { get; }

    public IWorldView World { get; }

    public PlannedWorld Planned { get; }

    public ICatalogue Catalogue { get; }

    public IGrid Grid { get; }

    public ISafetyFacts Facts { get; }

    public SafetyLimits Limits { get; }

    public Shapes Shapes { get; }

    public WorldModels Models { get; }

    public SafetyWarnings Warnings { get; } = new();

    /// <summary>The oxidiser gases, read once per pass.</summary>
    public HashSet<GasKind> Oxidisers => _oxidisers ??= Safety.Oxidisers.Kinds;

    /// <summary>The structures the controls check has looked at in this pass.</summary>
    public HashSet<ReferenceId> Looked { get; } = [];

    /// <summary>What the plan takes out, by the power network it is on (power cuts).</summary>
    public Dictionary<NetworkRef, List<(ReferenceId Id, GhostId Ghost)>> TakenOn { get; } = [];

    /// <summary>The networks of <see cref="TakenOn"/> in the order the plan's ghosts first take from them.</summary>
    public List<NetworkRef> PowerOrder { get; } = [];

    /// <summary>The structure a removal or swap ghost takes out.</summary>
    public static Option<StructureRef> Taken(Ghost ghost) => ghost.Intent switch
    {
        Intent.Remove remove => Option.Some(remove.Target),
        Intent.Swap swap => Option.Some(swap.Old),
        _ => Option<StructureRef>.None,
    };

    /// <summary>The piece a build or swap ghost puts in.</summary>
    public static Option<Placement> Put(Ghost ghost) => ghost.Intent switch
    {
        Intent.Build build => Option.Some(build.Target),
        Intent.Swap swap => Option.Some(swap.New),
        _ => Option<Placement>.None,
    };

    /// <summary>The ghost a planned world structure stands for, when it is one of this plan's ghosts.</summary>
    public Option<GhostId> PlanGhostOf(StructureRef structure) =>
        Planned.AssumedFrom(structure).TryGet(out var ghost) && InPlan.Contains(ghost) ? Option.Some(ghost) : Option<GhostId>.None;

    /// <summary>The catalogue facts of a piece, cached; none for a piece the catalogue does not hold.</summary>
    public Option<PieceFacts> Piece(PrefabKey piece)
    {
        if (!_pieces.TryGetValue(piece, out var facts))
            _pieces[piece] = facts = Catalogue.Piece(piece).Match(static f => Option.Some(f), static _ => Option<PieceFacts>.None);
        return facts;
    }

    /// <summary>The placement's shape; none for a piece the catalogue does not hold.</summary>
    public Option<PlacedShape> Shape(Placement placement) =>
        Shapes.Of(placement).Match(static s => Option.Some(s), static _ => Option<PlacedShape>.None);

    /// <summary>The shape of a structure as it stands.</summary>
    public Option<PlacedShape> Shape(StructureInfo info) => Shape(new Placement(info.Ref.Piece, info.Ref.At, info.Turn));

    /// <summary>The large cell holding a cell of the given grid.</summary>
    public Cell LargeOf(Cell cell, GridSize size) => size == GridSize.Large ? cell : Grid.Snap(Grid.CentreOf(cell, size), GridSize.Large);

    /// <summary>The room a large cell is in, as the world reads it; none outside.</summary>
    public Option<RoomInfo> RoomAt(Cell large) =>
        World.RoomAt(large).TryGet(out var room) ? World.Room(room) : Option<RoomInfo>.None;

    /// <summary>The two large cells a face spot lies between: the one on its negative side, then the one on its positive side.</summary>
    public (Cell Negative, Cell Positive) SidesOf(SpotKey face) => Faces.Sides(face, Grid);

    /// <summary>Whether the slot is one of a large cell's faces.</summary>
    public static bool IsFace(Slot slot) => slot is Slot.FaceXPlus or Slot.FaceXMinus or Slot.FaceYPlus or Slot.FaceYMinus or Slot.FaceZPlus or Slot.FaceZMinus;

    /// <summary>The face between two adjacent large cells, as each of them keys it.</summary>
    public Boundary FaceBetween(Cell large, Direction toward) => Faces.Between(large, toward, Grid);

    public static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Text(Cell cell) => $"{Text(cell.X)},{Text(cell.Y)},{Text(cell.Z)}";
}

/// <summary>
/// The warnings of one pass, one per cause (a code and what it is about: a room, a network, a door), each naming every
/// ghost behind it. The first report of a cause gives its facts; later reports add their ghosts. In the order the
/// causes were first reported; ghosts in id order.
/// </summary>
internal sealed class SafetyWarnings
{
    private readonly List<Cause> _causes = [];
    private readonly Dictionary<(ReasonCode, string), Cause> _byKey = [];

    public void Add(ReasonCode code, string about, IReadOnlyDictionary<string, string> facts, GhostId ghost)
    {
        if (!_byKey.TryGetValue((code, about), out var cause))
        {
            cause = new Cause(code, facts);
            _byKey[(code, about)] = cause;
            _causes.Add(cause);
        }

        if (!cause.Ghosts.Contains(ghost))
            cause.Ghosts.Add(ghost);
    }

    public void Add(ReasonCode code, string about, IReadOnlyDictionary<string, string> facts, IEnumerable<GhostId> ghosts)
    {
        foreach (var ghost in ghosts)
            Add(code, about, facts, ghost);
    }

    public ValueList<SafetyWarning> ToList()
    {
        var warnings = new List<SafetyWarning>(_causes.Count);
        foreach (var cause in _causes)
        {
            cause.Ghosts.Sort(static (a, b) => a.Value.CompareTo(b.Value));
            warnings.Add(new SafetyWarning(
                Reason.Of(cause.Code, SafetyCodes.Rule, ValueMap.From(cause.Facts)),
                ValueList.From(cause.Ghosts)));
        }

        return ValueList.From(warnings);
    }

    private sealed class Cause(ReasonCode code, IReadOnlyDictionary<string, string> facts)
    {
        public ReasonCode Code { get; } = code;

        public IReadOnlyDictionary<string, string> Facts { get; } = facts;

        public List<GhostId> Ghosts { get; } = new(1);
    }
}

/// <summary>The built world as consumers read it, over the world view; no assumed structures, no selectors.</summary>
internal sealed class ViewQuery(IWorldView world) : IWorldQuery
{
    public Option<StructureInfo> Structure(ReferenceId id) => world.Structure(id);

    public ValueList<StructureInfo> At(Cell cell) => world.At(cell);

    public Result<ValueList<StructureRef>, Reason> Select(StructureSelector selector) =>
        Result<ValueList<StructureRef>, Reason>.Error(
            Reason.Of(ReasonCodes.ModuleMissing, SafetyCodes.Rule, ValueMap.Of("module", "selectors")));

    public Option<NetworkRef> NetworkOf(ReferenceId id) => world.NetworkOf(id);

    public Option<RoomRef> RoomAt(Cell cell) => world.RoomAt(cell);

    public Option<GhostId> AssumedFrom(StructureRef structure) => Option<GhostId>.None;
}
