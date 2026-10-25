using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Swaps;
using Lattice.Tests.Board;
using Lattice.Tests.Validation;
using Xunit;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Swaps;

/// <summary>The swap planner's face check as validation runs it: registered like any rule, a warning on the swap ghost.</summary>
public sealed class SwapValidationTests
{
    [Fact]
    public void Validation_warns_on_a_window_swap_over_the_face_rating_with_the_numbers()
    {
        var bench = new ValidationBench();
        var wall = bench.Built("StructureWallIron", Large(0, 0, 0));
        var face = ValidationBench.Grid.Neighbour(Large(0, 0, 0), Direction.ZMinus, GridSize.Large);
        bench.World.SetPressureDifference(face, Direction.ZPlus, new Kilopascals(120));
        bench.Rules.Register(new SwapFaceRule(new SwapPlanner(Pieces, bench.Shapes, bench.World)).Descriptor);
        bench.Start();
        var window = new Placement(Pieces.Named("StructureCompositeWindowIron").Key, Large(0, 0, 0), Turn.None);
        var swap = Assert.Single(TestBoard.Applied(bench.Apply(new Command.Place(ValueList.Of(
            TestBoard.Draft(new Intent.Swap(wall, window, Option<Finish>.None)))))).Added).Id;

        bench.Pump();

        var why = Assert.Single(Warnings(bench.Report(), swap), w => w.Code == ReasonCodes.FaceOverRating);
        Assert.Equal("40", why.Facts["rating"]);
        Assert.Equal("120", why.Facts["difference"]);
    }
}
