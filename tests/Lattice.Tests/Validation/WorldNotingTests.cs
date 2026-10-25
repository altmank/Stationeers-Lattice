using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Validation;

/// <summary>
/// Which game answers a world change drops: those about cells in the fine buckets of the changed structure's cells and
/// the buckets around them (one large cell on every axis), each asked again once, however many changes came before
/// validation's next frame. A row of seven ghosts two large cells apart along x (apart, so the game is asked about each:
/// a planned neighbour holds the ask back); changes one large cell beside the row on z. Ghosts are named by their large x.
/// </summary>
public sealed class WorldNotingTests
{
    private const int Row = 7;

    private static (ValidationBench Bench, int Asked) RowOfGhosts(params Cell[] standing)
    {
        var bench = new ValidationBench();
        foreach (var at in standing)
            bench.Built("StructureFrameIron", at);
        bench.Start();
        PlaceRow(bench);
        bench.Pump();
        Assert.Equal(Row, bench.Placement.Calls.Count);
        return (bench, bench.Placement.Calls.Count);
    }

    private static void PlaceRow(ValidationBench bench)
    {
        for (var i = 0; i < Row; i++)
            bench.Ghost("StructureFrameIron", Large(2 * i, 0, 0));
    }

    // The large x of every ghost the game was asked about since the first `asked` calls, in order, repeats kept.
    private static List<int> AskedAgain(ValidationBench bench, int asked) =>
        bench.Placement.Calls.Skip(asked).Select(p => (p.At.X - 10) / 20).OrderBy(x => x).ToList();

    [Fact]
    public void A_structure_built_beside_the_row_asks_again_only_the_ghosts_within_a_large_cell_of_it_once_each()
    {
        var (bench, asked) = RowOfGhosts();

        bench.Built("StructureFrameIron", Large(5, 0, 1));
        bench.Pump();

        Assert.Equal(new[] { 4, 6 }, AskedAgain(bench, asked));
    }

    [Fact]
    public void A_structure_removed_beside_the_row_asks_again_the_ghosts_near_where_it_stood()
    {
        var (bench, asked) = RowOfGhosts(Large(5, 0, 1));

        bench.World.Remove(bench.World.All.Single().Ref.Id);
        bench.Pump();

        Assert.Equal(new[] { 4, 6 }, AskedAgain(bench, asked));
    }

    [Fact]
    public void A_structure_moved_along_the_row_asks_again_the_ghosts_near_its_old_place_and_near_its_new_one()
    {
        var (bench, asked) = RowOfGhosts(Large(1, 0, 1));
        var standing = bench.World.All.Single();

        bench.World.Move(ValueList.Of(new StructureMove(standing.Ref, Large(9, 0, 1), standing.Turn)));
        bench.Pump();

        Assert.Equal(new[] { 0, 2, 8, 10 }, AskedAgain(bench, asked));
    }

    [Fact]
    public void Changes_before_one_frame_ask_again_each_ghost_near_any_of_them_once()
    {
        var (bench, asked) = RowOfGhosts();

        bench.Built("StructureFrameIron", Large(1, 0, 1));
        bench.Built("StructureFrameIron", Large(2, 0, 1));
        bench.Built("StructureFrameIron", Large(12, 0, 1));
        bench.Built("StructureFrameIron", Large(1, 0, -1));
        bench.Pump();

        Assert.Equal(new[] { 0, 2, 12 }, AskedAgain(bench, asked));
    }

    [Fact]
    public void Changes_in_frames_of_their_own_each_ask_again_and_an_answer_given_after_a_change_is_kept()
    {
        var (bench, asked) = RowOfGhosts();

        bench.Built("StructureFrameIron", Large(5, 0, 1));
        bench.Pump();
        bench.Built("StructureFrameIron", Large(6, 0, -1));
        bench.Pump();
        bench.Pump();

        Assert.Equal(new[] { 4, 6, 6 }, AskedAgain(bench, asked));
    }

    [Fact]
    public void A_change_with_no_answer_near_it_asks_nothing_again()
    {
        var (bench, asked) = RowOfGhosts();

        bench.Built("StructureFrameIron", Large(5, 0, 3));
        bench.Pump();

        Assert.Empty(AskedAgain(bench, asked));
    }

    [Fact]
    public void A_change_after_the_asks_are_queued_and_before_they_are_answered_drops_none_of_their_answers()
    {
        var bench = new ValidationBench().Start();
        PlaceRow(bench);
        bench.Events.Flush();
        bench.Service.Frame();
        bench.Worker.RunAll();
        bench.Service.Frame();

        bench.Built("StructureFrameIron", Large(5, 0, 1));
        bench.Scheduler.RunFrame();
        Assert.Equal(Row, bench.Placement.Calls.Count);
        bench.Pump();

        Assert.Empty(AskedAgain(bench, Row));
    }

    [Fact]
    public void A_change_after_the_answers_are_given_and_before_validation_s_next_frame_drops_the_ones_near_it()
    {
        var bench = new ValidationBench().Start();
        PlaceRow(bench);
        bench.Frame();
        Assert.Equal(Row, bench.Placement.Calls.Count);

        bench.Built("StructureFrameIron", Large(5, 0, 1));
        bench.Pump();

        Assert.Equal(new[] { 4, 6 }, AskedAgain(bench, Row));
    }
}
