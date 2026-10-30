using Lattice.Api;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A planner turning a piece to the face it is aimed at, against the contract alone: how the game places the piece,
/// from its entry, with a case it does not know placed as a cell piece.
/// </summary>
public sealed class PiecePlacingTests
{
    private static string AimFor(PieceEntry piece) => piece.Placing switch
    {
        PiecePlacing.Face => "the face aimed at",
        PiecePlacing.FaceMount => "the face of the structure aimed at",
        _ => "the cell aimed into",
    };

    [Fact]
    public void A_planner_aims_each_piece_by_how_the_game_places_it()
    {
        var frame = new PieceEntry(new PrefabKey(1), "Frame", "Frame", PieceGrid.Large, ValueList<KitPlace>.Empty);
        var wall = frame with { Key = new PrefabKey(2), Placing = PiecePlacing.OnFace };
        var light = frame with { Key = new PrefabKey(3), Grid = PieceGrid.Small, Placing = PiecePlacing.OnFaceMount };

        Assert.Equal("the cell aimed into", AimFor(frame));
        Assert.Equal("the face aimed at", AimFor(wall));
        Assert.Equal("the face of the structure aimed at", AimFor(light));
    }
}
