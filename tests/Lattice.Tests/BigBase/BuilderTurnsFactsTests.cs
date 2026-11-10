using System.Collections.Generic;
using Lattice.Game.Live;

namespace Lattice.Tests.BigBase;

/// <summary>The pure parts of the builder-turns run: the turns the builder's keys reach and the placement log line.</summary>
public sealed class BuilderTurnsFactsTests
{
    // Four turns about one axis: Left adds one, Right subtracts one.
    private static int Quarter(int turn, TurnMove move) => move switch
    {
        TurnMove.Left => (turn + 1) % 4,
        TurnMove.Right => (turn + 3) % 4,
        _ => -1,
    };

    [Fact]
    public void The_keys_reach_every_turn_of_the_cycle_from_where_the_cursor_starts()
    {
        var reached = BuilderTurnsFacts.Reach(2, [TurnMove.Left, TurnMove.Right], Quarter);
        Assert.Equal(new List<int> { 2, 3, 1, 0 }, reached);
    }

    [Fact]
    public void A_key_that_gives_no_known_turn_adds_none_and_a_piece_with_no_keys_has_its_start_only()
    {
        Assert.Equal(new List<int> { 5 }, BuilderTurnsFacts.Reach(5, [TurnMove.Smart], Quarter));
        Assert.Equal(new List<int> { 5 }, BuilderTurnsFacts.Reach(5, [], Quarter));
    }

    [Fact]
    public void A_placement_line_names_the_piece_the_turn_and_the_outcome()
    {
        Assert.Equal("builder-turns: StructureWallFlat turn 4 placed at 1 2 3", BuilderTurnsFacts.PlacementLine("StructureWallFlat", 4, true, "at 1 2 3"));
        Assert.Equal("builder-turns: StructureWallFlat turn 7 refused the builder's gate: blocked", BuilderTurnsFacts.PlacementLine("StructureWallFlat", 7, false, "the builder's gate: blocked"));
        Assert.Equal("builder-turns: X turn 0 refused", BuilderTurnsFacts.PlacementLine("X", 0, false, ""));
    }
}
