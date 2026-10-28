using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Board;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// <see cref="IPieceCatalogue.CellsAt"/>: the cells a piece fills at a placement, from its footprint, turned with it, so
/// a planner sees how far a piece hangs below the cell it is placed at.
/// </summary>
public sealed class CellsAtTests
{
    private const string HangingText = """
        lattice-catalogue 3
        piece ModHangingTank kit=ItemModHangingTank option=0 grid=small
          step material=ItemModHangingTank*1 exit=ItemWrench air=no
          end 0 0 0 y+ gas
          cell 0 0 0 pipe
          cell 0 -5 0 pipe
          cell 0 -10 0 pipe
          cell 5 -10 0 pipe
          collision 1
        """;

    private static readonly Cell At = new(110, 110, 110);

    [Fact]
    public void A_piece_whose_body_hangs_below_its_cell_fills_the_cells_below_it_once_each_in_footprint_order()
    {
        var (catalogue, view) = Catalogue(HangingText);

        var cells = Prefabs.Ok(view.CellsAt(new Placement(catalogue.Named("ModHangingTank").Key, At, default)));

        Assert.Equal(new[] { At, new Cell(110, 105, 110), new Cell(110, 100, 110), new Cell(115, 100, 110) }, cells);
        Assert.Equal(At.Y - 10, Lowest(cells));
    }

    [Fact]
    public void A_turn_turns_the_cells_about_the_placements_cell()
    {
        var (catalogue, view) = Catalogue(HangingText);
        var grid = new CubeGrid();
        var turn = TestBoard.TurnSending(Direction.YMinus, Direction.YPlus, grid);
        var key = catalogue.Named("ModHangingTank").Key;

        var cells = Prefabs.Ok(view.CellsAt(new Placement(key, At, turn)));

        var expected = new List<Cell>();
        foreach (var offset in new[] { new CellOffset(0, 0, 0), new CellOffset(0, -5, 0), new CellOffset(0, -10, 0), new CellOffset(5, -10, 0) })
            expected.Add(At.Plus(grid.Apply(turn, offset)));
        Assert.Equal(expected, cells);
        Assert.Equal(At.Y, Lowest(cells));
    }

    [Fact]
    public void A_piece_with_no_footprint_fills_its_own_cell_and_an_unknown_piece_is_refused_unknown_piece()
    {
        var (catalogue, view) = Catalogue(HangingText);

        Assert.Equal(new[] { At }, Prefabs.Ok(view.CellsAt(new Placement(catalogue.Named("StructureFrame").Key, At, default))));
        Assert.True(view.CellsAt(new Placement(new PrefabKey(12345), At, default)).TryGetError(out var why));
        Assert.Equal(ReasonCodes.UnknownPiece, why.Code);
    }

    [GameCatalogueFact]
    public void The_small_insulated_in_line_liquid_tank_hangs_ten_decimetres_below_its_cell()
    {
        var catalogue = GameCatalogue.Load();
        var view = new PieceCatalogue(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue));

        var cells = Prefabs.Ok(view.CellsAt(new Placement(PrefabHash.Piece("StructureLiquidTankSmallInsulatedInLine"), At, default)));

        Assert.Contains(At, cells);
        Assert.Equal(At.Y - 10, Lowest(cells));
        Assert.Equal(new HashSet<Cell>(cells).Count, cells.Count);
    }

    private static int Lowest(ValueList<Cell> cells)
    {
        var lowest = int.MaxValue;
        foreach (var cell in cells)
            lowest = System.Math.Min(lowest, cell.Y);
        return lowest;
    }

    private static (FakeCatalogue Catalogue, PieceCatalogue View) Catalogue(string text)
    {
        var extra = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(text), "cells.lcat")).Pieces;
        var catalogue = new TestBoard(default, [.. extra]).Catalogue;
        return (catalogue, new PieceCatalogue(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue)));
    }
}
