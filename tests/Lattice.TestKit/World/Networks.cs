using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.TestKit.Grid;

namespace Lattice.TestKit.World;

/// <summary>
/// Cable, pipe and chute networks derived from the catalogue's connection ends: two pieces join when each has an end
/// sharing a network kind in its own cell pointing at the other's cell; a device (a piece whose ends all have a role,
/// or that sits in a small cell's device slot, as the game's <c>SmallCell.Device</c>: a battery's data end has no role)
/// joins the network of a piece whose end faces one of its ports through a shared kind
/// (connection is geometric). Every piece is on exactly one
/// network, a lone piece on its own. A network's ref is its smallest piece id; its rating is its weakest piece's; its
/// cells are the cells its pieces occupy, in piece id order. Load, volume, pressure and contents are not derived.
/// </summary>
internal sealed class Networks
{
    private readonly Dictionary<ReferenceId, NetworkRef> _of = [];
    private readonly Dictionary<NetworkRef, NetworkInfo> _networks = [];

    public static Networks Empty { get; } = new();

    public IReadOnlyDictionary<NetworkRef, NetworkInfo> All => _networks;

    public Option<NetworkRef> Of(ReferenceId id) => _of.TryGetValue(id, out var network) ? Option.Some(network) : Option<NetworkRef>.None;

    public Option<NetworkInfo> Info(NetworkRef network) =>
        _networks.TryGetValue(network, out var info) ? Option.Some(info) : Option<NetworkInfo>.None;

    private static bool IsDevice(PieceFacts piece)
    {
        foreach (var cell in piece.Footprint)
            if (cell.Slot == Slot.Device)
                return true;
        return false;
    }

    public static Networks Derive(IEnumerable<StructureInfo> structures, ICatalogue catalogue, IReadOnlyDictionary<ReferenceId, Cell[]> occupies)
    {
        var ends = new Dictionary<(Cell, Direction), List<(ReferenceId Id, ConnectionEnd End)>>();
        var pieceKinds = new Dictionary<ReferenceId, ConnectionEnd>();
        var facts = new Dictionary<ReferenceId, PieceFacts>();
        var worldEnds = new List<(ReferenceId Id, Cell Cell, Direction Facing, ConnectionEnd End, int Step)>();
        foreach (var s in structures)
        {
            if (!catalogue.Piece(s.Ref.Piece).TryGet(out var piece) || piece.Ends.Count == 0)
                continue;
            facts.Add(s.Ref.Id, piece);
            var device = IsDevice(piece);
            foreach (var end in piece.Ends)
            {
                if (end.Role.IsNone && !device && !pieceKinds.ContainsKey(s.Ref.Id))
                    pieceKinds.Add(s.Ref.Id, end);
                var cell = s.Ref.At.Plus(FakeGrid.Instance.Apply(s.Turn, end.At));
                var facing = FakeGrid.Instance.Apply(s.Turn, end.Facing);
                var key = (cell, facing);
                if (!ends.TryGetValue(key, out var list))
                    ends.Add(key, list = []);
                list.Add((s.Ref.Id, end));
                worldEnds.Add((s.Ref.Id, cell, facing, end, FakeGrid.Step(piece.Grid)));
            }
        }

        var parent = new Dictionary<ReferenceId, ReferenceId>();
        foreach (var id in pieceKinds.Keys)
            parent.Add(id, id);
        var devicesOf = new List<(ReferenceId Piece, ReferenceId Device)>();
        foreach (var (id, cell, facing, end, step) in worldEnds)
        {
            if (end.Role.IsSome || !pieceKinds.ContainsKey(id))
                continue;
            var next = cell.Plus(Offset(facing, step));
            if (!ends.TryGetValue((next, Opposite(facing)), out var facingBack))
                continue;
            foreach (var (other, otherEnd) in facingBack)
            {
                if (!otherEnd.SharesAKindWith(end))
                    continue;
                if (otherEnd.Role.IsNone && pieceKinds.ContainsKey(other))
                    Union(parent, id, other);
                else if (!pieceKinds.ContainsKey(other))
                    devicesOf.Add((id, other));
            }
        }

        var pieces = new Dictionary<ReferenceId, List<ReferenceId>>();
        foreach (var id in pieceKinds.Keys)
        {
            var root = Find(parent, id);
            if (!pieces.TryGetValue(root, out var members))
                pieces.Add(root, members = []);
            members.Add(id);
        }

        var devices = new Dictionary<ReferenceId, SortedSet<long>>();
        foreach (var (piece, device) in devicesOf)
        {
            var root = Find(parent, piece);
            if (!devices.TryGetValue(root, out var set))
                devices.Add(root, set = []);
            set.Add(device.Value);
        }

        var result = new Networks();
        foreach (var (root, members) in pieces)
        {
            members.Sort(static (a, b) => a.Value.CompareTo(b.Value));
            var network = new NetworkRef(members[0].Value);
            var kinds = pieceKinds[root].Kinds;
            var rating = Option<Watts>.None;
            var cells = new List<Cell>();
            foreach (var member in members)
            {
                result._of.Add(member, network);
                if (occupies.TryGetValue(member, out var occupied))
                    foreach (var cell in occupied)
                        if (!cells.Contains(cell))
                            cells.Add(cell);
                if (kinds.Contains(new NetworkType.Power()) && facts[member].Ratings.Cable.TryGet(out var watts) &&
                    (!rating.TryGet(out var weakest) || watts.Value < weakest.Value))
                    rating = Option.Some(watts);
            }

            var deviceIds = new List<ReferenceId>();
            if (devices.TryGetValue(root, out var deviceSet))
                foreach (var device in deviceSet)
                {
                    deviceIds.Add(new ReferenceId(device));
                    result._of.TryAdd(new ReferenceId(device), network);
                }

            result._networks.Add(network, new NetworkInfo(
                network,
                kinds,
                ValueList.From(members),
                ValueList.From(deviceIds),
                ValueList.From(cells),
                Option<Watts>.None,
                rating,
                Option<Litres>.None,
                Option<Kilopascals>.None,
                Option<ValueList<GasHeld>>.None));
        }

        return result;
    }

    private static CellOffset Offset(Direction facing, int step)
    {
        var v = Vector3i.Of(facing).Times(step);
        return new CellOffset(v.X, v.Y, v.Z);
    }

    private static Direction Opposite(Direction d) => Vector3i.Of(d).Times(-1).ToDirection();

    private static ReferenceId Find(Dictionary<ReferenceId, ReferenceId> parent, ReferenceId id)
    {
        while (parent[id] != id)
            id = parent[id] = parent[parent[id]];
        return id;
    }

    private static void Union(Dictionary<ReferenceId, ReferenceId> parent, ReferenceId a, ReferenceId b)
    {
        var ra = Find(parent, a);
        var rb = Find(parent, b);
        if (ra != rb)
            parent[rb] = ra;
    }
}
