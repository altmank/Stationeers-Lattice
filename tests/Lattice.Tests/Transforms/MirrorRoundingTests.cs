using System;
using Lattice.Api;
using Lattice.Core.Transforms;

namespace Lattice.Tests.Transforms;

/// <summary>
/// Mirroring small pieces in integer decimetres (<c>2 * At - x</c>) against the game's own rounding. The game snaps a
/// small piece to every multiple of 0.5 m (<c>ExtensionMethods.GridCenter(pos, 0.5, 0.25)</c>:
/// <c>Mathf.Round((x - 0.5) / 0.5) * 0.5 + 0.5</c>, Mathf.Round taking .5 to the even integer; decompile
/// Assets.Scripts.Util/ExtensionMethods.cs:1092, SmallGrid.cs:28) and keys the cell by <c>Grid3</c>, round(pos * 10)
/// with .5 away from zero (Assets.Scripts.GridSystem/Grid3.cs:117, 173).
/// </summary>
public sealed class MirrorRoundingTests
{
    // Grid3.Round: (int)(value + 0.5f) at or above 0, (int)(value - 0.5f) below.
    private static int Grid3Round(float value) => value >= 0f ? (int)(value + 0.5f) : (int)(value - 0.5f);

    // GridCenter(pos, 0.5, 0.25) per axis; Mathf.Round is Math.Round to even.
    private static float SmallCentre(float metres) => (float)Math.Round((metres - 0.5f) / 0.5f, MidpointRounding.ToEven) * 0.5f + 0.5f;

    // The cell the game keys a small piece at, aimed at a position in metres.
    private static int GameKey(float metres) => Grid3Round(SmallCentre(metres) * 10f);

    [Fact]
    public void Grid3_rounds_two_and_a_half_decimetres_to_three_away_from_zero()
    {
        Assert.Equal(3, Grid3Round(0.25f * 10f));
        Assert.Equal(-3, Grid3Round(-0.25f * 10f));
        Assert.Equal(18, Grid3Round(1.75f * 10f));
    }

    [Fact]
    public void A_small_piece_at_a_quarter_metre_is_snapped_to_a_half_metre_centre_before_it_is_keyed()
    {
        Assert.Equal(5, GameKey(0.25f));
        Assert.Equal(15, GameKey(1.75f));
        Assert.Equal(-5, GameKey(-0.25f));
    }

    [Fact]
    public void Every_small_centre_mirrored_across_a_whole_metre_plane_lands_on_the_games_key_for_the_mirrored_position()
    {
        var b = new TransformBench();
        var cable = b.T.Catalogue.Named("StructureCableStraight").Key;
        for (var plane = -30; plane <= 30; plane += 10)
            for (var x = -40; x <= 40; x += 5)
            {
                var image = b.Placements.Apply(new Transformation.Mirror(new MirrorPlane(Axis.X, plane)), new Placement(cable, new Cell(x, 0, 0), default));
                Assert.True(image.TryGet(out var mirrored), $"x {x} across {plane}: {image}");
                Assert.Equal(GameKey(2f * plane / 10f - x / 10f), mirrored.At.X);
            }
    }

    [Fact]
    public void A_piece_keyed_at_a_quarter_metre_is_refused_off_grid_not_mirrored_to_a_key_the_game_would_not_give()
    {
        var b = new TransformBench();
        var cable = b.T.Catalogue.Named("StructureCableStraight").Key;
        var plane = new MirrorPlane(Axis.X, 10);

        var image = b.Placements.Apply(new Transformation.Mirror(plane), new Placement(cable, new Cell(Grid3Round(0.25f * 10f), 0, 0), default));

        Assert.True(image.TryGetError(out var problem));
        Assert.Equal(TransformProblem.OffGrid, problem);
        Assert.NotEqual(2 * plane.At - 3, Grid3Round((2f * plane.At / 10f - 0.25f) * 10f));
    }
}
