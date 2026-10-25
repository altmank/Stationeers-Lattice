using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Ports;

namespace Lattice.Core.Selectors;

/// <summary>
/// Structure selectors on the built world: references, box (the mirror's chunks of space), run, piece and their and/or
/// from the mirror's indices;
/// networks and rooms from the world view (a network's pieces, a room's cells), then the mirror. Nothing scans the
/// world. Answers are in id order and kept per selector until the mirror changes, so a repeated read of an unchanged
/// world allocates nothing. Main thread.
/// </summary>
internal sealed class StructureSelection(Shapes shapes, IGrid grid, WorldMirror mirror, IWorldView world)
{
    private readonly GridSpace _space = new(grid);
    private readonly StructurePieces _pieces = new(mirror, shapes);
    private readonly Dictionary<StructureSelector, Result<ValueList<StructureRef>, Reason>> _answers = new();
    private readonly List<ReferenceId> _found = new();
    private long _answersAt = -1;

    /// <summary>
    /// The structures a selector picks, in id order; refused <see cref="ReasonCodes.UnknownKind"/> for a kind this
    /// version does not know, inside an and/or too. Cost: as each case says; a repeated selector on an unchanged world
    /// is a lookup.
    /// </summary>
    public Result<ValueList<StructureRef>, Reason> Select(StructureSelector selector)
    {
        if (_answersAt != mirror.Revision)
        {
            _answers.Clear();
            _answersAt = mirror.Revision;
        }

        if (!_answers.TryGetValue(selector, out var answer))
            _answers[selector] = answer = Pick(selector).Map(Sorted);
        return answer;
    }

    private Result<HashSet<ReferenceId>, Reason> Pick(StructureSelector selector) => selector switch
    {
        StructureSelector.Refs refs => Ok(Existing(refs.Values)),
        StructureSelector.Box box => Ok(InBox(CellBox.Between(box.Corner, box.Opposite))),
        StructureSelector.RunThrough run => Ok(Walks.Run(_pieces, run.Cell, grid)),
        StructureSelector.Network network => Ok(OnNetwork(network.Of)),
        StructureSelector.Room room => Ok(InRoom(room.Of)),
        StructureSelector.OfPiece piece => Ok(new HashSet<ReferenceId>(mirror.OfPiece(piece.Piece))),
        StructureSelector.All all => Combine(all.Of, intersect: true),
        StructureSelector.Any any => Combine(any.Of, intersect: false),
        _ => Result<HashSet<ReferenceId>, Reason>.Error(SelectorRules.Unknown(selector)),
    };

    /// <summary>The structures whose own cell is in the box. Linear in what the box's chunks of space hold, never in the world.</summary>
    private HashSet<ReferenceId> InBox(CellBox box)
    {
        _found.Clear();
        mirror.InBox(box.Min, box.Max, _found);
        var ids = new HashSet<ReferenceId>(_found);
        _found.Clear();
        return ids;
    }

    /// <summary>The structures standing that the references name: by id, else by piece and cell. Linear in the references.</summary>
    private HashSet<ReferenceId> Existing(ValueList<StructureRef> refs)
    {
        var found = new HashSet<ReferenceId>();
        foreach (var structure in refs)
            if (mirror.Find(structure).TryGet(out var info))
                found.Add(info.Ref.Id);
        return found;
    }

    /// <summary>The network's pieces (not its devices); a network the world does not know picks nothing. Linear in its pieces.</summary>
    private HashSet<ReferenceId> OnNetwork(NetworkRef network)
    {
        var found = new HashSet<ReferenceId>();
        if (world.Network(network).TryGet(out var info))
            foreach (var id in info.Pieces)
                if (mirror.Structure(id).IsSome)
                    found.Add(id);
        return found;
    }

    /// <summary>Structures in or bounding a room (<see cref="Picks.Room"/>); a room the world does not know picks nothing.</summary>
    private HashSet<ReferenceId> InRoom(RoomRef room) =>
        world.Room(room).TryGet(out var info) ? Picks.Room(_pieces, info.Cells, _space) : new HashSet<ReferenceId>();

    /// <summary>And of none is nothing; or of none is nothing.</summary>
    private Result<HashSet<ReferenceId>, Reason> Combine(ValueList<StructureSelector> parts, bool intersect)
    {
        HashSet<ReferenceId>? combined = null;
        foreach (var part in parts)
        {
            var picked = Pick(part);
            if (!picked.TryGet(out var ids))
                return picked;
            if (combined is null)
                combined = ids;
            else if (intersect)
                combined.IntersectWith(ids);
            else
                combined.UnionWith(ids);
        }

        return Ok(combined ?? new HashSet<ReferenceId>());
    }

    private ValueList<StructureRef> Sorted(HashSet<ReferenceId> ids)
    {
        var keys = new long[ids.Count];
        var refs = new StructureRef[ids.Count];
        var at = 0;
        foreach (var id in ids)
        {
            if (!mirror.Structure(id).TryGet(out var info))
                continue;
            keys[at] = id.Value;
            refs[at++] = info.Ref;
        }

        if (at < refs.Length)
        {
            Array.Resize(ref keys, at);
            Array.Resize(ref refs, at);
        }

        Array.Sort(keys, refs);
        return Lists.Own(refs);
    }

    private static Result<HashSet<ReferenceId>, Reason> Ok(HashSet<ReferenceId> ids) => Result<HashSet<ReferenceId>, Reason>.Ok(ids);
}
