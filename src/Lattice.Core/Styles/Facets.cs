using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Selectors;

namespace Lattice.Core.Styles;

/// <summary>
/// Judges facets of drawn things. Local cases read the thing's own facts; relational selectors (network, room, run
/// through, joined, or an and/or holding one) read the ghosts the board picked as a whole. Ghost-only cases (state, finding,
/// status, intent, mine, tinted, draft, pending, selectors) are false for a structure. Findings read the cached
/// validation answer the styles keep from verdict changes: drawing never starts a check, and plan state's background
/// checks cover every plan. Allocates nothing.
/// </summary>
internal sealed class Facets(Actor viewer, StyleSets sets, Relations relations)
{
    public Actor Viewer { get; } = viewer;

    /// <summary>Whether the match is a leaf: not a combinator, not <c>Everything</c>, and a case this Lattice knows.</summary>
    public static bool IsLeaf(StyleMatch match) => match is StyleMatch.Picks or StyleMatch.Intent or StyleMatch.Part
        or StyleMatch.State or StyleMatch.Finding or StyleMatch.Status or StyleMatch.Family or StyleMatch.InSet
        or StyleMatch.Mine or StyleMatch.Tinted or StyleMatch.Finishes or StyleMatch.Draft or StyleMatch.Pending or StyleMatch.Structure
        or StyleMatch.Facing;

    /// <summary>A selector the board answers as a whole: network, room, run through, joined, or an and/or holding one.</summary>
    public static bool IsRelational(Selector selector) => selector switch
    {
        Selector.Network or Selector.Room or Selector.RunThrough or Selector.Joined => true,
        Selector.All all => AnyRelational(all.Of),
        Selector.Any any => AnyRelational(any.Of),
        _ => false,
    };

    private static bool AnyRelational(ValueList<Selector> selectors)
    {
        foreach (var selector in selectors)
            if (IsRelational(selector))
                return true;
        return false;
    }

    /// <summary>The leaf kind of a match leaf.</summary>
    public static LeafKind KindOf(StyleMatch match) => match switch
    {
        StyleMatch.Picks => LeafKind.Picks,
        StyleMatch.Intent => LeafKind.Intent,
        StyleMatch.Part => LeafKind.Part,
        StyleMatch.State => LeafKind.State,
        StyleMatch.Finding => LeafKind.Finding,
        StyleMatch.Status => LeafKind.Status,
        StyleMatch.Family => LeafKind.Family,
        StyleMatch.InSet => LeafKind.InSet,
        StyleMatch.Mine => LeafKind.Mine,
        StyleMatch.Tinted => LeafKind.Tinted,
        StyleMatch.Finishes => LeafKind.Finishes,
        StyleMatch.Draft => LeafKind.Draft,
        StyleMatch.Pending => LeafKind.Pending,
        StyleMatch.Structure => LeafKind.Structure,
        StyleMatch.Facing => LeafKind.Facing,
        _ => LeafKind.Unknown,
    };

    public bool Holds(Leaf leaf, Thing thing)
    {
        if (leaf.Retired)
            return false;
        var structure = thing.Kind == ThingKind.Structure;
        var match = leaf.Match;
        return leaf.Kind switch
        {
            LeafKind.Highlight => leaf.Highlight!.Holds(thing),
            LeafKind.Picks => !structure && Picks(((StyleMatch.Picks)match!).Which, thing, leaf),
            LeafKind.Intent => thing.Intent is { } asked && asked.Kind == ((StyleMatch.Intent)match!).Kind,
            LeafKind.Part => thing.Part == ((StyleMatch.Part)match!).Kind,
            LeafKind.State => !structure && StateOf(thing) == ((StyleMatch.State)match!).Kind,
            LeafKind.Finding => !structure && FindingHolds((StyleMatch.Finding)match!, thing),
            LeafKind.Status => !structure && StatusHolds((StyleMatch.Status)match!, thing),
            LeafKind.Family => FamilyTree.Holds(thing.Families, ((StyleMatch.Family)match!).Of),
            LeafKind.Facing => thing.FaceAxis == ((StyleMatch.Facing)match!).Normal,
            LeafKind.InSet => sets.Holds(((StyleMatch.InSet)match!).Set, thing),
            LeafKind.Mine => !structure && (thing.Kind == ThingKind.Draft || thing.Record is { } ghost && ghost.PlacedBy == Viewer),
            LeafKind.Tinted => !structure && thing.Tint.IsSome,
            LeafKind.Finishes => !structure && FinishOf(thing.Intent) is not null,
            LeafKind.Draft => thing.Kind == ThingKind.Draft,
            LeafKind.Pending => thing.Kind == ThingKind.Draft && thing.Pending,
            LeafKind.Structure => structure,
            _ => false,
        };
    }

    /// <summary>
    /// Judges any match on a thing's facts directly, with no signature (a pick's filter). A relational selector is
    /// picked afresh when the board moved since it last was.
    /// </summary>
    public bool Judge(StyleMatch match, Thing thing, LeafBook leaves) => match switch
    {
        StyleMatch.Everything => true,
        StyleMatch.All all => AllHold(all.Of, thing, leaves),
        StyleMatch.Any any => AnyHolds(any.Of, thing, leaves),
        StyleMatch.Not not => !Judge(not.Match, thing, leaves),
        StyleMatch.Picks picks when IsRelational(picks.Which) => Holds(match, thing, relations.Fresh(leaves.Find(match))),
        _ => IsLeaf(match) && Holds(match, thing, null),
    };

    private bool AllHold(ValueList<StyleMatch> matches, Thing thing, LeafBook leaves)
    {
        for (var i = 0; i < matches.Count; i++)
            if (!Judge(matches[i], thing, leaves))
                return false;
        return true;
    }

    private bool AnyHolds(ValueList<StyleMatch> matches, Thing thing, LeafBook leaves)
    {
        for (var i = 0; i < matches.Count; i++)
            if (Judge(matches[i], thing, leaves))
                return true;
        return false;
    }

    private bool Holds(StyleMatch match, Thing thing, Leaf? leaf)
    {
        var structure = thing.Kind == ThingKind.Structure;
        return match switch
        {
            StyleMatch.Picks picks => !structure && Picks(picks.Which, thing, leaf),
            StyleMatch.Intent intent => thing.Intent is { } asked && asked.Kind == intent.Kind,
            StyleMatch.Part part => thing.Part == part.Kind,
            StyleMatch.State state => !structure && StateOf(thing) == state.Kind,
            StyleMatch.Finding finding => !structure && FindingHolds(finding, thing),
            StyleMatch.Status status => !structure && StatusHolds(status, thing),
            StyleMatch.Family family => FamilyTree.Holds(thing.Families, family.Of),
            StyleMatch.Facing facing => thing.FaceAxis == facing.Normal,
            StyleMatch.InSet inSet => sets.Holds(inSet.Set, thing),
            StyleMatch.Mine => !structure && (thing.Kind == ThingKind.Draft || thing.Record is { } ghost && ghost.PlacedBy == Viewer),
            StyleMatch.Tinted => !structure && thing.Tint.IsSome,
            StyleMatch.Finishes => !structure && FinishOf(thing.Intent) is not null,
            StyleMatch.Draft => thing.Kind == ThingKind.Draft,
            StyleMatch.Pending => thing.Kind == ThingKind.Draft && thing.Pending,
            StyleMatch.Structure => structure,
            _ => false,
        };
    }

    /// <summary>The finish an intent carries: a finish-only ghost's, or a build's or swap's when it has one; null otherwise.</summary>
    public static Finish? FinishOf(Intent? intent) => intent switch
    {
        Intent.FinishOnly only => only.Finish,
        Intent.Build build when build.Finish.TryGet(out var finish) => finish,
        Intent.Swap swap when swap.Finish.TryGet(out var finish) => finish,
        _ => null,
    };

    private static StateKind StateOf(Thing thing) => thing.Record?.State switch
    {
        GhostState.Started => StateKind.Started,
        GhostState.Stale => StateKind.Stale,
        _ => StateKind.Planned,
    };

    public static FindingKind KindOf(Finding? finding) => finding switch
    {
        Api.Finding.Passes => FindingKind.Passes,
        Api.Finding.WaitsFor => FindingKind.WaitsFor,
        Api.Finding.WillFail => FindingKind.WillFail,
        Api.Finding.Warns => FindingKind.Warns,
        _ => FindingKind.Unchecked,
    };

    private static bool FindingHolds(StyleMatch.Finding match, Thing thing)
    {
        if (KindOf(thing.Finding) != match.Kind)
            return false;
        if (!match.Code.TryGet(out var code))
            return true;
        return thing.Finding switch
        {
            Api.Finding.WillFail fail => fail.Why.Code == code,
            Api.Finding.Warns warns => warns.Why.Code == code,
            _ => false,
        };
    }

    private static bool StatusHolds(StyleMatch.Status match, Thing thing)
    {
        var reports = thing.Statuses;
        var reported = false;
        for (var i = 0; i < reports.Count; i++)
        {
            var report = reports[i];
            if (match.By.TryGet(out var by) && report.By.Id != by)
                continue;
            reported = true;
            if (KindOf(report.Status) == match.Kind)
                return true;
        }

        return match.Kind == StatusKind.None && !reported;
    }

    private static StatusKind KindOf(ExecutorStatus status) => status switch
    {
        ExecutorStatus.Working => StatusKind.Working,
        ExecutorStatus.Waiting => StatusKind.Waiting,
        ExecutorStatus.Paused => StatusKind.Paused,
        _ => StatusKind.None,
    };

    private bool Picks(Selector selector, Thing thing, Leaf? leaf)
    {
        if (leaf is { Relational: true })
            return thing.Kind == ThingKind.Ghost && leaf.Picked is { } picked && picked.Contains(thing.Id);
        return Local(selector, thing);
    }

    /// <summary>A local selector on a ghost's or draft's own facts, as the board decides it (and of none: nothing).</summary>
    private bool Local(Selector selector, Thing thing)
    {
        var ghost = thing.Record;
        var raw = thing.Raw;
        switch (selector)
        {
            case Selector.Everything:
                return true;
            case Selector.Ids ids:
                return thing.Kind == ThingKind.Ghost && Contains(ids.Values, thing.Id);
            case Selector.Box box:
                return CellBox.Between(box.Corner, box.Opposite).Contains(thing.At);
            case Selector.Group group:
                return ghost is not null ? ghost.Groups.Contains(group.Name) : raw is not null && raw.Groups.Contains(group.Name);
            case Selector.Owner owner:
                return ghost is not null ? ghost.PlacedBy == owner.Actor : owner.Actor == Viewer;
            case Selector.MadeBy madeBy:
                return ghost is not null ? ghost.MadeBy == madeBy.Maker : raw is not null && raw.MadeBy == madeBy.Maker;
            case Selector.InPlan inPlan:
                return ghost is not null ? ghost.Plan == inPlan.Plan : raw is not null && raw.Plan.Reduce(GhostBoard.DefaultPlan) == inPlan.Plan;
            case Selector.Tagged tagged:
                var attached = ghost is not null ? ghost.Attached : raw?.Attached;
                return attached is not null && attached.TryGetValue(tagged.Key, out var held) && (!tagged.Value.TryGet(out var wanted) || held == wanted);
            case Selector.OfPiece piece:
                return thing.Piece == piece.Piece;
            case Selector.All all:
                if (all.Of.Count == 0)
                    return false;
                for (var i = 0; i < all.Of.Count; i++)
                    if (!Local(all.Of[i], thing))
                        return false;
                return true;
            case Selector.Any any:
                for (var i = 0; i < any.Of.Count; i++)
                    if (Local(any.Of[i], thing))
                        return true;
                return false;
            default:
                return false;
        }
    }

    private static bool Contains(ValueList<GhostId> ids, GhostId id)
    {
        for (var i = 0; i < ids.Count; i++)
            if (ids[i] == id)
                return true;
        return false;
    }
}
