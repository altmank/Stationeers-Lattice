using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Tests.Grid;

/// <summary>
/// How two cells of one grid touch, where a large cell's faces are, and where a small cell sits in a large cell.
/// A large cell holds small centres at +0.5, +1.0 and +1.5 m from its minimum face and shares the one on each face
/// plane with its neighbour; a large cell's face point
/// is half a cell from its centre (decompile <c>Assets.Scripts.GridSystem/Grid3.cs</c>, <c>Grid3.Face</c>).
/// </summary>
public sealed class CellRelationTests
{
    private static readonly Cell Centre = new(10, 10, 10);

    [Fact]
    public void A_cell_is_the_same_as_itself() =>
        Assert.IsType<Contact.Same>(CellRelations.Between(Centre, Centre, GridSize.Large));

    [Fact]
    public void Cells_one_step_apart_share_a_face() =>
        Assert.Equal(new Contact.Face(Direction.ZMinus), CellRelations.Between(Centre, new Cell(10, 10, -10), GridSize.Large));

    [Fact]
    public void Cells_one_step_apart_on_two_axes_share_an_edge() =>
        Assert.Equal(
            new Contact.Edge(Direction.XPlus, Direction.YMinus),
            CellRelations.Between(Centre, new Cell(15, 5, 10), GridSize.Small));

    [Fact]
    public void Cells_one_step_apart_on_three_axes_share_a_corner() =>
        Assert.Equal(
            new Contact.Corner(Direction.XMinus, Direction.YPlus, Direction.ZPlus),
            CellRelations.Between(Centre, new Cell(-10, 30, 30), GridSize.Large));

    [Theory]
    [InlineData(50, 10, 10)]
    [InlineData(30, 10, 50)]
    [InlineData(20, 10, 10)]
    public void Cells_further_apart_do_not_touch(int x, int y, int z) =>
        Assert.IsType<Contact.Apart>(CellRelations.Between(Centre, new Cell(x, y, z), GridSize.Large));

    [Fact]
    public void A_large_cells_face_point_is_half_a_cell_from_its_centre()
    {
        Assert.Equal(new Cell(20, 10, 10), CellRelations.FaceOf(Centre, Direction.XPlus));
        Assert.Equal(new Cell(10, 0, 10), CellRelations.FaceOf(Centre, Direction.YMinus));
    }

    [Fact]
    public void Neighbours_share_one_face_point()
    {
        var neighbour = new Cell(10, 10, 30);

        Assert.Equal(CellRelations.FaceOf(Centre, Direction.ZPlus), CellRelations.FaceOf(neighbour, Direction.ZMinus));
    }

    [Fact]
    public void A_large_cell_holds_27_small_cells_inside()
    {
        var inside = 0;
        for (var x = 0; x <= 20; x += 5)
            for (var y = 0; y <= 20; y += 5)
                for (var z = 0; z <= 20; z += 5)
                    if (CellRelations.PlaceOf(new Cell(x, y, z), Centre) is SmallPlace.Inside)
                        inside++;

        Assert.Equal(27, inside);
    }

    [Fact]
    public void A_small_cell_on_a_face_plane_is_on_that_face() =>
        Assert.Equal(new SmallPlace.OnFace(Direction.YPlus), CellRelations.PlaceOf(new Cell(5, 20, 15), Centre));

    [Fact]
    public void A_small_cell_on_two_face_planes_is_on_their_edge() =>
        Assert.Equal(
            new SmallPlace.OnEdge(Direction.XMinus, Direction.ZPlus),
            CellRelations.PlaceOf(new Cell(0, 10, 20), Centre));

    [Fact]
    public void A_small_cell_on_three_face_planes_is_on_a_corner() =>
        Assert.Equal(
            new SmallPlace.OnCorner(Direction.XPlus, Direction.YMinus, Direction.ZMinus),
            CellRelations.PlaceOf(new Cell(20, 0, 0), Centre));

    [Fact]
    public void A_small_cell_past_a_face_plane_is_outside() =>
        Assert.IsType<SmallPlace.Outside>(CellRelations.PlaceOf(new Cell(25, 10, 10), Centre));

    [Fact]
    public void A_small_cell_on_a_shared_face_is_on_both_cells_faces()
    {
        var onFace = new Cell(20, 15, 5);

        Assert.Equal(new SmallPlace.OnFace(Direction.XPlus), CellRelations.PlaceOf(onFace, Centre));
        Assert.Equal(new SmallPlace.OnFace(Direction.XMinus), CellRelations.PlaceOf(onFace, new Cell(30, 10, 10)));
    }
}
