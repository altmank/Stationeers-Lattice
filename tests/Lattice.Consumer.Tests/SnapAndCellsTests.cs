using Lattice.Api;
using Lattice.Testing.Stubs;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A planner keeping an engine on the game's snap and resting a tank on a frame, against the contract alone: a piece's
/// snap from its entry, and the cells it fills from a stubbed catalogue.
/// </summary>
public sealed class SnapAndCellsTests
{
    private static readonly PieceSnap FrameCentres = new(new AxisSnap(2f, 0f), new AxisSnap(0.5f, 0.25f), new AxisSnap(2f, 0f));

    private sealed class Hanging : StubPieceCatalogue
    {
        public override Result<ValueList<Cell>, Reason> CellsAt(Placement placement) =>
            Result<ValueList<Cell>, Reason>.Ok(ValueList.From(new[] { placement.At, placement.At.Plus(new CellOffset(0, -10, 0)) }));
    }

    [Fact]
    public void A_planner_moves_its_aim_to_a_cell_the_game_snaps_the_piece_to()
    {
        var engine = new PieceEntry(new PrefabKey(7), "Engine", "Engine", PieceGrid.Small, ValueList<KitPlace>.Empty) { Snap = FrameCentres };
        var aim = new Cell(115, 110, 110);

        var at = engine.Snap.Holds(aim) ? aim : aim with { X = engine.Snap.X.Key(aim.X / 10f) };

        Assert.Equal(Option.Some(Axis.X), engine.Snap.OffAxis(aim));
        Assert.True(engine.Snap.Holds(at));
        Assert.Equal(PieceSnap.Of(PieceGrid.Small), new PieceEntry(new PrefabKey(8), "Cable", "Cable", PieceGrid.Small, ValueList<KitPlace>.Empty).Snap);
    }

    [Fact]
    public void A_planner_rests_a_hanging_tank_on_a_frame_by_its_lowest_cell()
    {
        IPieceCatalogue catalogue = new Hanging();
        var frameTop = 120;

        var cells = catalogue.CellsAt(new Placement(new PrefabKey(9), new Cell(110, 0, 110), default)).Match(static c => c, static _ => ValueList<Cell>.Empty);
        var hangs = 0;
        foreach (var cell in cells)
            hangs = System.Math.Max(hangs, 0 - cell.Y);

        Assert.Equal(10, hangs);
        Assert.Equal(130, frameTop + hangs);
    }
}
