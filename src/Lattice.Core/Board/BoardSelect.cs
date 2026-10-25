using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Board;

/// <summary>
/// The selectors module as the board calls it: the selectors that read shapes, attachments or the built world.
/// </summary>
internal interface IGhostSelectors
{
    /// <summary>Rises whenever an answer of <see cref="Pick"/> may change without a board commit (the built world changed).</summary>
    long Revision { get; }

    /// <summary>The ghosts a box, run, network, room or tag selector picks; refused for any other kind.</summary>
    Result<HashSet<GhostId>, Reason> Pick(BoardIndex index, Selector selector);
}

/// <summary>
/// The selectors the board answers from its own indices: everything, ids, plan, group, piece, owner, maker, and
/// their and/or.
/// Box, run, network, room and tag selectors go to the installed selectors module, refused
/// <see cref="ReasonCodes.ModuleMissing"/> while none is. Cost: linear in the indexed sets it reads.
/// </summary>
internal static class BoardSelect
{
    private static readonly ModuleName Selectors = ModuleName.Create("selectors")
        .Match(static name => name, static reason => throw new System.InvalidOperationException(reason.ToString()));

    public static Result<HashSet<GhostId>, Reason> Ids(BoardIndex index, Selector selector, IGhostSelectors? module) => selector switch
    {
        Selector.Everything => Ok(new HashSet<GhostId>(index.Ghosts.Keys)),
        Selector.Ids ids => Ok(Existing(index, ids.Values)),
        Selector.InPlan inPlan => Ok(new HashSet<GhostId>(index.InPlan(inPlan.Plan))),
        Selector.Group group => Ok(new HashSet<GhostId>(index.InGroup(group.Name))),
        Selector.OfPiece piece => Ok(new HashSet<GhostId>(index.OfPiece(piece.Piece))),
        Selector.Owner owner => Ok(new HashSet<GhostId>(index.OwnedBy(owner.Actor))),
        Selector.MadeBy madeBy => Ok(new HashSet<GhostId>(index.MadeBy(madeBy.Maker))),
        Selector.All all => Combine(index, all.Of, intersect: true, module),
        Selector.Any any => Combine(index, any.Of, intersect: false, module),
        Selector.Box or Selector.RunThrough or Selector.Network or Selector.Room or Selector.Tagged => module is null
            ? Result<HashSet<GhostId>, Reason>.Error(Reason.Of(
                ReasonCodes.ModuleMissing, LatticeIdentity.Board, ValueMap.Of("module", Selectors.Value)))
            : module.Pick(index, selector),
        _ => Result<HashSet<GhostId>, Reason>.Error(Unknown.Kind(selector)),
    };

    private static HashSet<GhostId> Existing(BoardIndex index, ValueList<GhostId> ids)
    {
        var found = new HashSet<GhostId>();
        foreach (var id in ids)
            if (index.Ghosts.ContainsKey(id))
                found.Add(id);
        return found;
    }

    /// <summary>And of none is nothing; or of none is nothing.</summary>
    private static Result<HashSet<GhostId>, Reason> Combine(BoardIndex index, ValueList<Selector> parts, bool intersect, IGhostSelectors? module)
    {
        HashSet<GhostId>? combined = null;
        foreach (var part in parts)
        {
            var picked = Ids(index, part, module);
            if (!picked.TryGet(out var ids))
                return picked;
            if (combined is null)
                combined = ids;
            else if (intersect)
                combined.IntersectWith(ids);
            else
                combined.UnionWith(ids);
        }

        return Ok(combined ?? new HashSet<GhostId>());
    }

    private static Result<HashSet<GhostId>, Reason> Ok(HashSet<GhostId> ids) => Result<HashSet<GhostId>, Reason>.Ok(ids);
}
