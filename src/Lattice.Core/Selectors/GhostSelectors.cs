using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Ports;

namespace Lattice.Core.Selectors;

/// <summary>The selectors module's rule id, for its refusals.</summary>
internal static class SelectorRules
{
    public static readonly RuleId Selectors = RuleId.Known("lattice.selectors");

    public static Reason Unknown(object selector) =>
        Reason.Of(ReasonCodes.UnknownKind, Selectors, ValueMap.Of("kind", selector.GetType().Name));
}

/// <summary>
/// The selectors module on ghosts: box, run, network, room and tag selectors, installed into the board
/// (<see cref="GhostBoard.InstallSelectors"/>). Runs follow the board's cell index; networks and rooms read the world
/// view for the network's pieces and the room's cells, and the mirror for the structures; nothing scans the world.
/// Its <see cref="Revision"/> is the mirror's, so views drop cached network and room answers when the world changes.
/// Main thread.
/// </summary>
internal sealed class GhostSelectors(Shapes shapes, IGrid grid, WorldMirror mirror, IWorldView world) : IGhostSelectors
{
    private readonly GridSpace _space = new(grid);
    private readonly StructurePieces _structures = new(mirror, shapes);

    public long Revision => mirror.Revision;

    public Result<HashSet<GhostId>, Reason> Pick(BoardIndex index, Selector selector) => selector switch
    {
        Selector.Box box => Ok(Picks.Box(new GhostPieces(index), CellBox.Between(box.Corner, box.Opposite), _space)),
        Selector.RunThrough run => Ok(Walks.Run(new GhostPieces(index), run.Cell, grid)),
        Selector.Network network => Ok(OnNetwork(index, network.Of)),
        Selector.Room room => Ok(InRoom(index, room.Of)),
        Selector.Tagged tagged => Ok(Tagged(index, tagged.Key, tagged.Value)),
        _ => Result<HashSet<GhostId>, Reason>.Error(SelectorRules.Unknown(selector)),
    };

    /// <summary>
    /// Ghosts on a network: those pointing at one of its pieces (remove, swap, finish, started), and build and swap
    /// ghosts joined end to end to its pieces, directly or through each other, by ends of the network's kinds. A
    /// network the world does not know picks nothing. Linear in the network's pieces and the ghosts reached.
    /// </summary>
    private HashSet<GhostId> OnNetwork(BoardIndex index, NetworkRef network)
    {
        var picked = new HashSet<GhostId>();
        if (!world.Network(network).TryGet(out var info))
            return picked;

        var seeds = new List<PlacedShape>(info.Pieces.Count);
        foreach (var id in info.Pieces)
        {
            if (!mirror.Structure(id).TryGet(out var structure) && !world.Structure(id).TryGet(out structure))
                continue;
            foreach (var ghost in index.OnStructure(structure.Ref))
                picked.Add(ghost);
            if (_structures.ShapeOf(structure).TryGet(out var shape))
                seeds.Add(shape);
        }

        picked.UnionWith(Walks.Joined(new GhostPieces(index), seeds, info.Kinds, grid));
        return picked;
    }

    /// <summary>Ghosts in or bounding a room (<see cref="Picks.Room"/>); a room the world does not know picks nothing.</summary>
    private HashSet<GhostId> InRoom(BoardIndex index, RoomRef room) =>
        world.Room(room).TryGet(out var info) ? Picks.Room(new GhostPieces(index), info.Cells, _space) : new HashSet<GhostId>();

    /// <summary>Ghosts with data under the key, and with that value when one is given. Linear in the ghosts with the key.</summary>
    private static HashSet<GhostId> Tagged(BoardIndex index, AttachmentKey key, Option<Attachment> value)
    {
        var picked = new HashSet<GhostId>();
        foreach (var id in index.Tagged(key))
            if (!value.TryGet(out var wanted) || (index.Ghosts[id].Ghost.Attached.TryGetValue(key, out var held) && held == wanted))
                picked.Add(id);
        return picked;
    }

    private static Result<HashSet<GhostId>, Reason> Ok(HashSet<GhostId> ids) => Result<HashSet<GhostId>, Reason>.Ok(ids);
}
