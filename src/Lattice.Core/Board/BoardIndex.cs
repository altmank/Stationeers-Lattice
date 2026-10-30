using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Board;

/// <summary>
/// A ghost on the board with what the board derives from it: its placed shape (build and swap ghosts), the structure it
/// targets (remove, swap, finish), and the revision of the last change a command made to it.
/// </summary>
internal sealed class GhostEntry(Ghost ghost, Option<PlacedShape> shape, Option<StructureRef> target, Revision commandRevision)
{
    public Ghost Ghost { get; } = ghost;

    public Option<PlacedShape> Shape { get; } = shape;

    public Option<StructureRef> Target { get; } = target;

    /// <summary>Changed-since checks compare against this, never against marks Lattice makes from the world.</summary>
    public Revision CommandRevision { get; } = commandRevision;

    public GhostId Id => Ghost.Id;

    public GhostEntry With(Ghost changed, Option<Revision> byCommand) =>
        new(changed, Shape, Target, byCommand.Reduce(CommandRevision));
}

/// <summary>An active plan, the revision of the last change a command made to it, and its ghosts.</summary>
internal sealed class PlanEntry(Plan plan, Revision commandRevision)
{
    public Plan Plan { get; set; } = plan;

    public Revision CommandRevision { get; set; } = commandRevision;

    /// <summary>The plan's ghosts done so far (they left the board), across loads: saving restores it from the plan's file.</summary>
    public int Done { get; set; }

    /// <summary>
    /// Of <see cref="Done"/>, how many each executor held the claim on when they completed (<see cref="Completion.By"/>);
    /// kept beside it on every path.
    /// </summary>
    public DoneBy DoneBy { get; set; } = DoneBy.None;

    public HashSet<GhostId> Ghosts { get; } = new();
}

/// <summary>
/// Every index the board keeps, updated per change so no read scans: by id, spot, cell, chunk of space (a ghost's own
/// cell, for boxes), structure, plan, group, piece, owner, maker and attachment key. Keys with no ghost left are dropped, so memory follows the board.
/// </summary>
internal sealed class BoardIndex
{
    private readonly Dictionary<SpotKey, GhostId> _spots = new();
    private readonly Dictionary<ReferenceId, GhostId> _targets = new();
    private readonly Dictionary<Cell, HashSet<GhostId>> _cells = new();
    private readonly CellChunks<GhostId> _chunks = new();
    private readonly Dictionary<ReferenceId, HashSet<GhostId>> _structureIds = new();
    private readonly Dictionary<(PrefabKey, Cell), HashSet<GhostId>> _structurePlaces = new();
    private readonly Dictionary<GroupName, HashSet<GhostId>> _groups = new();
    private readonly Dictionary<PrefabKey, HashSet<GhostId>> _pieces = new();
    private readonly Dictionary<Actor, HashSet<GhostId>> _owners = new();
    private readonly Dictionary<Maker, HashSet<GhostId>> _makers = new();
    private readonly Dictionary<AttachmentKey, HashSet<GhostId>> _tags = new();

    public Dictionary<GhostId, GhostEntry> Ghosts { get; } = new();

    public Dictionary<PlanId, PlanEntry> Plans { get; } = new();

    public bool TryOccupant(SpotKey spot, out GhostId ghost) => _spots.TryGetValue(spot, out ghost);

    public bool TryTargeting(ReferenceId structure, out GhostId ghost) => _targets.TryGetValue(structure, out ghost);

    public IReadOnlyCollection<GhostId> InCell(Cell cell) => Get(_cells, cell);

    /// <summary>
    /// Adds to <paramref name="into"/>, in no set order, every ghost whose own cell (<see cref="OwnCell"/>) lies from
    /// <paramref name="min"/> to <paramref name="max"/> on every axis. Linear in the ghosts of the chunks the box overlaps.
    /// </summary>
    public void InBox(Cell min, Cell max, List<GhostId> into) => _chunks.InBox(min, max, into);

    /// <summary>A ghost's own cell: its new piece's (build, swap), else its target's (remove, finish).</summary>
    public static Cell OwnCell(GhostEntry entry) =>
        entry.Shape.TryGet(out var shape) ? shape.Placement.At : entry.Target.TryGet(out var target) ? target.At : default;

    /// <summary>By reference id, else by piece and cell (a structure the game gave a new id).</summary>
    public IReadOnlyCollection<GhostId> OnStructure(StructureRef structure) =>
        _structureIds.TryGetValue(structure.Id, out var byId) ? byId : Get(_structurePlaces, (structure.Piece, structure.At));

    public IReadOnlyCollection<GhostId> InGroup(GroupName group) => Get(_groups, group);

    /// <summary>Every group with a ghost in it, and its ghosts.</summary>
    public IReadOnlyDictionary<GroupName, HashSet<GhostId>> Groups => _groups;

    public IReadOnlyCollection<GhostId> OfPiece(PrefabKey piece) => Get(_pieces, piece);

    public IReadOnlyCollection<GhostId> OwnedBy(Actor actor) => Get(_owners, actor);

    public IReadOnlyCollection<GhostId> MadeBy(Maker maker) => Get(_makers, maker);

    /// <summary>The ghosts carrying attached data under the key.</summary>
    public IReadOnlyCollection<GhostId> Tagged(AttachmentKey key) => Get(_tags, key);

    public IReadOnlyCollection<GhostId> InPlan(PlanId plan) => Plans.TryGetValue(plan, out var entry) ? entry.Ghosts : System.Array.Empty<GhostId>();

    public void Add(GhostEntry entry)
    {
        var ghost = entry.Ghost;
        Ghosts.Add(entry.Id, entry);
        Plans[ghost.Plan].Ghosts.Add(entry.Id);
        _chunks.Add(entry.Id, OwnCell(entry));
        if (entry.Shape.TryGet(out var shape))
        {
            foreach (var spot in shape.Spots)
            {
                _spots[spot] = entry.Id;
                Put(_cells, spot.Cell, entry.Id);
            }

            Put(_cells, shape.Placement.At, entry.Id);
            Put(_pieces, shape.Placement.Piece, entry.Id);
        }

        if (entry.Target.TryGet(out var target))
        {
            _targets[target.Id] = entry.Id;
            Put(_cells, target.At, entry.Id);
        }

        foreach (var structure in Structures(entry))
        {
            Put(_structureIds, structure.Id, entry.Id);
            Put(_structurePlaces, (structure.Piece, structure.At), entry.Id);
        }

        foreach (var group in ghost.Groups)
            Put(_groups, group, entry.Id);
        Put(_owners, ghost.PlacedBy, entry.Id);
        Put(_makers, ghost.MadeBy, entry.Id);
        foreach (var attached in ghost.Attached)
            Put(_tags, attached.Key, entry.Id);
    }

    public void Remove(GhostEntry entry)
    {
        var ghost = entry.Ghost;
        Ghosts.Remove(entry.Id);
        _chunks.Remove(entry.Id, OwnCell(entry));
        if (Plans.TryGetValue(ghost.Plan, out var plan))
            plan.Ghosts.Remove(entry.Id);
        if (entry.Shape.TryGet(out var shape))
        {
            foreach (var spot in shape.Spots)
            {
                if (_spots.TryGetValue(spot, out var holder) && holder == entry.Id)
                    _spots.Remove(spot);
                Take(_cells, spot.Cell, entry.Id);
            }

            Take(_cells, shape.Placement.At, entry.Id);
            Take(_pieces, shape.Placement.Piece, entry.Id);
        }

        if (entry.Target.TryGet(out var target))
        {
            if (_targets.TryGetValue(target.Id, out var holder) && holder == entry.Id)
                _targets.Remove(target.Id);
            Take(_cells, target.At, entry.Id);
        }

        foreach (var structure in Structures(entry))
        {
            Take(_structureIds, structure.Id, entry.Id);
            Take(_structurePlaces, (structure.Piece, structure.At), entry.Id);
        }

        foreach (var group in ghost.Groups)
            Take(_groups, group, entry.Id);
        Take(_owners, ghost.PlacedBy, entry.Id);
        Take(_makers, ghost.MadeBy, entry.Id);
        foreach (var attached in ghost.Attached)
            Take(_tags, attached.Key, entry.Id);
    }

    private static List<StructureRef> Structures(GhostEntry entry)
    {
        var structures = new List<StructureRef>(2);
        if (entry.Target.TryGet(out var target))
            structures.Add(target);
        if (entry.Ghost.State is GhostState.Started started && !structures.Contains(started.PartBuilt))
            structures.Add(started.PartBuilt);
        return structures;
    }

    private static IReadOnlyCollection<GhostId> Get<TKey>(Dictionary<TKey, HashSet<GhostId>> index, TKey key) where TKey : notnull =>
        index.TryGetValue(key, out var set) ? set : System.Array.Empty<GhostId>();

    private static void Put<TKey>(Dictionary<TKey, HashSet<GhostId>> index, TKey key, GhostId ghost) where TKey : notnull
    {
        if (!index.TryGetValue(key, out var set))
            index[key] = set = new HashSet<GhostId>();
        set.Add(ghost);
    }

    private static void Take<TKey>(Dictionary<TKey, HashSet<GhostId>> index, TKey key, GhostId ghost) where TKey : notnull
    {
        if (index.TryGetValue(key, out var set) && set.Remove(ghost) && set.Count == 0)
            index.Remove(key);
    }
}

/// <summary>A plan's done ghosts per executor that held their claim; immutable, so a count is set or rolled back whole.</summary>
internal sealed class DoneBy
{
    private readonly Dictionary<ExecutorId, int> _counts;

    private DoneBy(Dictionary<ExecutorId, int> counts) => _counts = counts;

    /// <summary>No ghost done by an executor.</summary>
    public static DoneBy None { get; } = new(new Dictionary<ExecutorId, int>());

    public static DoneBy From(ValueMap<ExecutorId, int> counts)
    {
        if (counts.Count == 0)
            return None;
        var copy = new Dictionary<ExecutorId, int>(counts.Count);
        foreach (var pair in counts)
            copy[pair.Key] = pair.Value;
        return new DoneBy(copy);
    }

    public int Count => _counts.Count;

    public int Of(ExecutorId by) => _counts.TryGetValue(by, out var done) ? done : 0;

    /// <summary>The counts with one more (or, for a negative step, fewer) for <paramref name="by"/>.</summary>
    public DoneBy Plus(ExecutorId by, int step)
    {
        var copy = new Dictionary<ExecutorId, int>(_counts);
        var next = Of(by) + step;
        if (next > 0)
            copy[by] = next;
        else
            copy.Remove(by);
        return new DoneBy(copy);
    }

    public ValueMap<ExecutorId, int> ToMap() => ValueMap<ExecutorId, int>.From(_counts);

    public Dictionary<ExecutorId, int>.Enumerator GetEnumerator() => _counts.GetEnumerator();
}
