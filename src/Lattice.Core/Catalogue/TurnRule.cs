using System.Globalization;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Matching;
using Lattice.Core.Ports;

namespace Lattice.Core.Catalogue;

/// <summary>
/// Whether a piece stands in a turn the game's builder can give it (<see cref="PieceFacts.AllowedTurns"/>): the one
/// check behind the turn rule, runs and routes, and transforms. A piece in another turn is one the game will not build
/// so.
/// </summary>
internal static class BuilderTurnChecks
{
    /// <summary>Who gives the turn-not-allowed reasons.</summary>
    public static RuleId Rule => RuleId.Known("lattice.turn");

    /// <summary>
    /// <see cref="ReasonCodes.TurnNotAllowed"/> for a piece placed in a turn the builder cannot give it, with facts
    /// <c>piece</c>, <c>cell</c> and <c>turn</c>; none in an allowed turn. Constant cost; allocates only the reason.
    /// </summary>
    public static Option<Reason> NotAllowed(PieceFacts piece, Placement placement)
    {
        if (piece.AllowedTurns.Contains(placement.Turn))
            return Option<Reason>.None;
        var at = placement.At;
        return Option.Some(Reason.Of(ReasonCodes.TurnNotAllowed, Rule, ValueMap
            .Of("piece", piece.Key.Hash.ToString(CultureInfo.InvariantCulture))
            .With("cell", $"{at.X},{at.Y},{at.Z}")
            .With("turn", placement.Turn.Index.ToString(CultureInfo.InvariantCulture))));
    }
}

/// <summary>
/// Lattice's own validation rule for turns: a build or swap ghost whose new piece stands in a turn the game's builder
/// cannot give it fails <see cref="ReasonCodes.TurnNotAllowed"/> (<see cref="BuilderTurnChecks.NotAllowed"/>), so it is
/// never built so. A piece with no direction (<see cref="TurnFreePieces"/>) passes in any turn: the game builds it in an
/// allowed one and it matches. Registered inline: called on the validation worker as each ghost is judged; it holds
/// its own <see cref="TurnFreePieces"/>, which only the worker asks. Constant cost once a piece's freedom is known; a
/// pass allocates nothing.
/// </summary>
internal sealed class TurnRule(ICatalogue catalogue, TurnFreePieces turnFree)
{
    private static readonly RuleVerdict Passed = new RuleVerdict.Pass();

    /// <summary>The ghosts it is asked about: builds and swaps, the ghosts that put a piece.</summary>
    public static bool Applies(Ghost ghost) => ghost.Intent is Intent.Build or Intent.Swap;

    /// <summary>The descriptor to register with the validation rules.</summary>
    public RuleDescriptor Descriptor => new(BuilderTurnChecks.Rule, Check) { Thread = CallbackThread.Worker };

    /// <summary>The verdict for one ghost: a failure in a turn its piece's builder cannot give, else a pass.</summary>
    public RuleVerdict Check(RuleInput input)
    {
        var put = input.Ghost.Intent switch
        {
            Intent.Build build => build.Target,
            Intent.Swap swap => swap.New,
            _ => null,
        };
        if (put is null || !catalogue.Piece(put.Piece).TryGet(out var piece) || !BuilderTurnChecks.NotAllowed(piece, put).TryGet(out var why))
            return Passed;
        return turnFree.Has(piece.Key) ? Passed : new RuleVerdict.Fail(why);
    }
}
