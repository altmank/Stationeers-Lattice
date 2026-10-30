using System.Globalization;
using Lattice.Api;

namespace Lattice.Core.Swaps;

/// <summary>
/// Lattice's own validation rule for swaps onto a face: a swap ghost whose new piece is rated below the face's pressure
/// difference now warns <see cref="ReasonCodes.FaceOverRating"/>, with the numbers (facts <c>piece</c>, <c>rating</c>
/// and <c>difference</c> in kPa, <c>cell</c>, <c>face</c>). A warning, not a failure: the game builds the piece, then
/// damages it every tick the difference stays above its rating.
/// A piece with no face rating has no limit and gets nothing. Main thread, as it reads the world view.
/// </summary>
internal sealed class SwapFaceRule(SwapPlanner planner)
{
    /// <summary>The descriptor to register with the validation rules.</summary>
    public RuleDescriptor Descriptor => new(SwapCodes.Swaps, Check) { Thread = CallbackThread.Main };

    /// <summary>The ghosts it is asked about: swaps; every other ghost passes it.</summary>
    public static bool Applies(Ghost ghost) => ghost.Intent is Intent.Swap;

    /// <summary>The verdict for one ghost: a warning for a swap over its face rating, else a pass. Linear in the new piece's spots.</summary>
    public RuleVerdict Check(RuleInput input)
    {
        if (input.Ghost.Intent is not Intent.Swap swap || !planner.FaceLoadOf(swap.New).TryGet(out var load) || !load.Over)
            return new RuleVerdict.Pass();

        return new RuleVerdict.Warn(Reason.Of(ReasonCodes.FaceOverRating, SwapCodes.Swaps, ValueMap
            .Of("piece", swap.New.Piece.Hash.ToString(CultureInfo.InvariantCulture))
            .With("rating", Text(load.Rating.Value))
            .With("difference", Text(load.Difference.Value))
            .With("cell", $"{load.Cell.X},{load.Cell.Y},{load.Cell.Z}")
            .With("face", Runs.Directions.Of(SwapPlanner.Positive(load.Normal)).ToString())));
    }

    private static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
