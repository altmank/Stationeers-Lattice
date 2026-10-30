using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;

namespace Lattice.Core.Matching;

/// <summary>What the world says of one ghost.</summary>
internal abstract record Verdict
{
    private Verdict() { }

    /// <summary>Nothing of it is built (again).</summary>
    public sealed record Planned : Verdict;

    /// <summary>Work on it is under way on that structure.</summary>
    public sealed record Started(StructureRef On) : Verdict;

    /// <summary>It cannot be done as it is.</summary>
    public sealed record Stale(Reason Why) : Verdict;

    /// <summary>The world matches it; <see cref="Built"/> is the structure that does, for a build, swap or finish.</summary>
    public sealed record Done(Option<StructureRef> Built) : Verdict;

    /// <summary>The structure it points at is gone and it can no longer be done.</summary>
    public sealed record TargetGone : Verdict;

    /// <summary>Not judged: a kind this version does not know.</summary>
    public sealed record Unjudged(Reason Why) : Verdict;
}

/// <summary>When a ghost is judged: in the one pass at load, or on a change while the world runs.</summary>
internal enum Moment
{
    Load = 1,
    Live = 2,
}

/// <summary>
/// Judges a ghost against the world mirror (Lattice builds what is represented). Reads only the
/// mirror, never the world. Main thread; linear in the ghost's spots and what stands in them.
/// <list type="bullet">
/// <item>Build: done when exactly its piece stands in its cell and turn (any turn for a piece with no direction), fully
/// built, with its finish, whatever else shares its spots; started while that structure is part built, or built but not
/// yet finished; stale (<see cref="ReasonCodes.Different"/>) when it is broken, or when it is not there and anything
/// else stands in its spots; planned when its spots are empty.</item>
/// <item>Remove: done when the structure is gone. A target is found by id; at load also by piece and cell.</item>
/// <item>Swap: planned while the old structure stands; once it is gone, judged as a build of the new piece. At load, a
/// swap whose old structure is gone and whose new piece is not done has lost its target, at load
/// too.</item>
/// <item>Finish: done when colour, name and setting match what it asks; its target gone, it has lost its target.</item>
/// </list>
/// Stale reasons carry the facts <c>structure</c> (the id), <c>piece</c> (its prefab hash) and <c>problem</c>:
/// <c>piece</c> (another piece), <c>cell</c> (its piece, another cell), <c>turn</c> (its piece and cell, another turn)
/// or <c>broken</c>.
/// </summary>
internal sealed class Matcher(WorldMirror world, TurnFreePieces turnFree)
{
    /// <summary>The rule id of matching's reasons.</summary>
    public static readonly RuleId Rule = RuleId.Known("lattice.matching");

    private static readonly Verdict PlannedVerdict = new Verdict.Planned();
    private static readonly Verdict GoneVerdict = new Verdict.TargetGone();

    private readonly List<StructureInfo> _here = new(4);

    public Verdict Judge(GhostEntry entry, Moment moment) => entry.Ghost.Intent switch
    {
        Intent.Build build => Build(build.Target, build.Finish, entry.Shape),
        Intent.Remove remove => Target(remove.Target, moment).IsSome
            ? PlannedVerdict
            : new Verdict.Done(Option<StructureRef>.None),
        Intent.Swap swap => Swap(swap, entry.Shape, moment),
        Intent.FinishOnly finish => Target(finish.Target, moment).TryGet(out var target)
            ? Finished(finish.Finish, target) ? new Verdict.Done(Option.Some(target.Ref)) : PlannedVerdict
            : GoneVerdict,
        _ => new Verdict.Unjudged(Unknown.Kind(entry.Ghost.Intent)),
    };

    private Verdict Swap(Intent.Swap swap, Option<PlacedShape> shape, Moment moment)
    {
        if (Target(swap.Old, moment).IsSome)
            return PlannedVerdict;
        var built = Build(swap.New, swap.Finish, shape);
        return moment == Moment.Load && built is not Verdict.Done ? GoneVerdict : built;
    }

    /// <summary>
    /// The structure a ghost points at. At load by id, else by piece and cell (the save may give it a new id); while the
    /// world runs by id only: once its id is gone it is gone, whatever is built in its place later.
    /// </summary>
    private Option<StructureInfo> Target(StructureRef target, Moment moment) =>
        moment == Moment.Load ? world.Find(target) : world.Structure(target.Id);

    // The ghost's own structure decides when it stands, whatever else shares its spots: the game accepted both, and
    // the verdict must not hang on which of them appeared first. Without it, anything in its spots makes it stale.
    private Verdict Build(Placement target, Option<Finish> finish, Option<PlacedShape> shape)
    {
        _here.Clear();
        if (shape.TryGet(out var placed))
            world.InSpots(placed.Spots, _here);
        else
            _here.AddRange(world.At(target.At));

        StructureInfo? match = null;
        StructureInfo? other = null;
        foreach (var info in _here)
        {
            if (match is null && IsTheOne(info, target))
                match = info;
            else
                other ??= info;
        }

        _here.Clear();
        if (match is null)
            return other is null ? PlannedVerdict : Stale(other, ProblemWith(other, target));
        return match.State switch
        {
            BuildState.Built => finish.TryGet(out var asked) && !Finished(asked, match)
                ? new Verdict.Started(match.Ref)
                : new Verdict.Done(Option.Some(match.Ref)),
            BuildState.Building => new Verdict.Started(match.Ref),
            BuildState.Broken => Stale(match, "broken"),
            _ => new Verdict.Unjudged(Unknown.Kind(match.State)),
        };
    }

    private bool IsTheOne(StructureInfo info, Placement target) =>
        info.Ref.Piece == target.Piece
        && info.Ref.At == target.At
        && turnFree.LookAlike(target.Piece, target.Turn, info.Turn);

    private static string ProblemWith(StructureInfo other, Placement target) =>
        other.Ref.Piece != target.Piece ? "piece"
        : other.Ref.At != target.At ? "cell"
        : "turn";

    private static bool Finished(Finish asked, StructureInfo info) =>
        (asked.Colour.IsNone || asked.Colour == info.Colour)
        && (asked.Name.IsNone || asked.Name == info.Name)
        && (asked.Setting.IsNone || asked.Setting == info.Setting);

    private static Verdict Stale(StructureInfo other, string problem) => new Verdict.Stale(Reason.Of(
        ReasonCodes.Different,
        Rule,
        ValueMap.Of("structure", other.Ref.Id.Value.ToString())
            .With("piece", other.Ref.Piece.Hash.ToString())
            .With("problem", problem)));
}
