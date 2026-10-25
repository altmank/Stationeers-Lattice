using Lattice.Api;
using Lattice.Core.Ports;
using static Lattice.Tests.Grid.Turns;

namespace Lattice.Tests.Grid;

/// <summary>
/// Snapping follows the game: a position goes to its cell centre with <c>GridCenter</c> (large 2 m with offset 0,
/// small 0.5 m with offset 0.25; <c>Mathf.Round</c>, so a tie goes to the even multiple), then the centre becomes a
/// decimetre key with <c>Grid3.Round</c> (half away from zero). Sources:
/// decompile <c>Assets.Scripts.Util/ExtensionMethods.cs</c>
/// <c>GridCenter</c>, <c>Assets.Scripts.GridSystem/Grid3.cs</c> <c>Grid3(Vector3, float, float)</c> and <c>Round</c>.
/// </summary>
public sealed class SnapTests
{
    [Theory]
    [InlineData(0.3f, 10)]
    [InlineData(1.99f, 10)]
    [InlineData(-0.3f, -10)]
    [InlineData(101.7f, 1010)]
    [InlineData(-37.2f, -370)]
    [InlineData(0f, 10)]
    [InlineData(2f, 10)]
    [InlineData(4f, 50)]
    [InlineData(6f, 50)]
    [InlineData(-2f, -30)]
    public void A_large_cell_is_centred_on_odd_metres(float metres, int key)
    {
        var expected = new Cell(key, key, key);

        Assert.Equal(expected, Cube.Snap(new WorldPosition(metres, metres, metres), GridSize.Large));
    }

    [Theory]
    [InlineData(0.3f, 5)]
    [InlineData(0.2f, 0)]
    [InlineData(-0.1f, 0)]
    [InlineData(-0.3f, -5)]
    [InlineData(101.6f, 1015)]
    [InlineData(-37.2f, -370)]
    [InlineData(0.25f, 5)]
    [InlineData(0.75f, 5)]
    [InlineData(1.25f, 15)]
    [InlineData(-0.25f, -5)]
    public void A_small_cell_is_centred_on_every_half_metre(float metres, int key)
    {
        var expected = new Cell(key, key, key);

        Assert.Equal(expected, Cube.Snap(new WorldPosition(metres, metres, metres), GridSize.Small));
    }

    [Fact]
    public void Each_axis_snaps_on_its_own()
    {
        Assert.Equal(new Cell(10, 30, -10), Cube.Snap(new WorldPosition(0.5f, 2.5f, -0.5f), GridSize.Large));
        Assert.Equal(new Cell(5, 25, -5), Cube.Snap(new WorldPosition(0.5f, 2.5f, -0.5f), GridSize.Small));
    }

    [Theory]
    [InlineData((int)GridSize.Large, 1010, 30, -370)]
    [InlineData((int)GridSize.Small, 1015, 0, -5)]
    public void A_cell_centre_snaps_back_to_its_cell(int gridSize, int x, int y, int z)
    {
        var grid = (GridSize)gridSize;
        var cell = new Cell(x, y, z);

        Assert.Equal(cell, Cube.Snap(Cube.CentreOf(cell, grid), grid));
    }

    [Fact]
    public void A_cell_centre_is_its_key_in_metres()
    {
        Assert.Equal(new WorldPosition(101f, 3f, -37f), Cube.CentreOf(new Cell(1010, 30, -370), GridSize.Large));
    }

    [Theory]
    [InlineData((int)GridSize.Large, 20)]
    [InlineData((int)GridSize.Small, 5)]
    public void A_neighbour_is_one_cell_along_the_direction(int gridSize, int step)
    {
        var grid = (GridSize)gridSize;
        var cell = new Cell(10, 10, 10);
        foreach (var d in Directions)
        {
            var u = Unit(d);
            Assert.Equal(cell.Plus(new CellOffset(u.X * step, u.Y * step, u.Z * step)), Cube.Neighbour(cell, d, grid));
        }
    }
}
