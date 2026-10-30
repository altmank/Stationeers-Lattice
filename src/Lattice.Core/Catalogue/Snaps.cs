using System.Globalization;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Catalogue;

/// <summary>
/// Whether a piece stands where the game snaps it (<see cref="PieceFacts.Snap"/>): the one check behind the snap rule,
/// runs and routes, and transforms. A piece off its snap is one the game will not build there.
/// </summary>
internal static class Snaps
{
    /// <summary>Who gives the off-snap reasons.</summary>
    public static RuleId Rule => RuleId.Known("lattice.snap");

    /// <summary>
    /// <see cref="ReasonCodes.OffSnap"/> for a piece placed off its snap, with facts <c>piece</c>, <c>cell</c> and
    /// <c>axis</c> (the first one off); none on its snap. Constant cost; allocates only the reason.
    /// </summary>
    public static Option<Reason> Off(PieceFacts piece, Cell at)
    {
        if (!piece.Snap.OffAxis(at).TryGet(out var axis))
            return Option<Reason>.None;
        return Option.Some(Reason.Of(ReasonCodes.OffSnap, Rule, ValueMap
            .Of("piece", piece.Key.Hash.ToString(CultureInfo.InvariantCulture))
            .With("cell", $"{at.X},{at.Y},{at.Z}")
            .With("axis", axis switch { Axis.X => "x", Axis.Y => "y", _ => "z" })));
    }
}

/// <summary>
/// Lattice's own validation rule for snaps: a build or swap ghost whose new piece stands off its snap fails
/// <see cref="ReasonCodes.OffSnap"/> (<see cref="Snaps.Off"/>), so it is never built there. The ghost stays where it is.
/// Registered inline: called on the validation worker as each ghost is judged, after the ghost's piece is read for its
/// ordering, so it reads no piece the worker has not read already. Constant cost; a pass allocates nothing.
/// </summary>
internal sealed class SnapRule(ICatalogue catalogue)
{
    private static readonly RuleVerdict Passed = new RuleVerdict.Pass();

    /// <summary>The ghosts it is asked about: builds and swaps, the ghosts that put a piece.</summary>
    public static bool Applies(Ghost ghost) => ghost.Intent is Intent.Build or Intent.Swap;

    /// <summary>The descriptor to register with the validation rules.</summary>
    public RuleDescriptor Descriptor => new(Snaps.Rule, Check) { Thread = CallbackThread.Worker };

    /// <summary>The verdict for one ghost: a failure off its piece's snap, else a pass.</summary>
    public RuleVerdict Check(RuleInput input)
    {
        var put = input.Ghost.Intent switch
        {
            Intent.Build build => build.Target,
            Intent.Swap swap => swap.New,
            _ => null,
        };
        if (put is null || !catalogue.Piece(put.Piece).TryGet(out var piece) || !Snaps.Off(piece, put.At).TryGet(out var why))
            return Passed;
        return new RuleVerdict.Fail(why);
    }
}

/// <summary>
/// A placement check that refuses a piece off its snap (<see cref="Snaps.Off"/>) or in a turn the game's builder cannot
/// give it (<see cref="BuilderTurnChecks.NotAllowed"/>) before it asks the game's: what a run or route lays through, so
/// its first failure names such a piece as the game's own refusals are named.
/// </summary>
internal sealed class SnapFirst(ICatalogue catalogue, IPlacementCheck game) : IPlacementCheck
{
    public Result<Unit, Reason> Check(Placement placement)
    {
        if (catalogue.Piece(placement.Piece).TryGet(out var piece) &&
            (Snaps.Off(piece, placement.At).TryGet(out var why) || BuilderTurnChecks.NotAllowed(piece, placement).TryGet(out why)))
            return Result<Unit, Reason>.Error(why);
        return game.Check(placement);
    }
}
