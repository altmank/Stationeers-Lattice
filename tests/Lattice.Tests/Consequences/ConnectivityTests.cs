using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Consequences;
using static Lattice.Tests.Consequences.Models;

namespace Lattice.Tests.Consequences;

/// <summary>
/// The game's connection rule. An end sits in the neighbour's cell and faces back into its
/// own; a link needs the other piece in the end's cell with an end of a shared type facing back.
/// </summary>
public sealed class ConnectivityTests
{
    private static readonly PipeContent Gas = new(1, false);
    private static readonly PipeContent Liquid = new(2, false);

    private static PieceModel Straight(long id, int x, NetworkTypes? types = null, PipeContent? content = null) =>
        Piece(id, [At(x, 0, 0)], [Towards(x, x + 1, types), Towards(x, x - 1, types)], content);

    private static PieceEnd Towards(int from, int to, NetworkTypes? types = null) =>
        new(At(to, 0, 0), At(from, 0, 0), types ?? PowerAndData, ConnectionRole.None);

    private static PieceModel Long(long id, int x, int length)
    {
        var cells = new Cell[length];
        for (var index = 0; index < length; index++)
            cells[index] = At(x + index, 0, 0);
        return Piece(id, cells, [Towards(x, x - 1), Towards(x + length - 1, x + length)]);
    }

    private static PieceModel Tee(long id = 1) => Piece(id, Origin, End(Origin, 1, 0, 0), End(Origin, -1, 0, 0), End(Origin, 0, 0, 1));

    private static PieceModel StraightX(long id, Cell cell) => Piece(id, cell, End(cell, 1, 0, 0), End(cell, -1, 0, 0));

    private static PieceModel StraightZ(long id, Cell cell) => Piece(id, cell, End(cell, 0, 0, 1), End(cell, 0, 0, -1));

    [Fact]
    public void Neighbours_in_a_row_link_both_ways()
    {
        var links = Connectivity.LinksTouching([Straight(1, 0), Straight(2, 1), Straight(3, 2)], Ids(2));

        Assert.Equal(4, links.Count);
        Assert.Contains(Link(1, 2), links);
        Assert.Contains(Link(2, 1), links);
        Assert.Contains(Link(2, 3), links);
        Assert.Contains(Link(3, 2), links);
    }

    [Fact]
    public void An_identical_replacement_changes_no_link()
    {
        List<PieceModel> before = [Straight(1, 0), Straight(2, 1), Straight(3, 2)];
        var twin = Piece(2, [At(1, 0, 0)], [Towards(1, 0), Towards(1, 2)]);
        List<PieceModel> after = [before[0], twin, before[2]];

        Assert.True(Connectivity.SameShape(before[1], twin));
        Assert.True(Connectivity.Compare(Connectivity.LinksTouching(before, Ids(2)), Connectivity.LinksTouching(after, Ids(2))).Same);
    }

    [Fact]
    public void A_replacement_with_an_extra_end_is_not_the_same_shape_and_adds_a_link()
    {
        var side = Piece(4, [At(1, 1, 0)], [new PieceEnd(At(1, 0, 0), At(1, 1, 0), PowerAndData, ConnectionRole.None)]);
        List<PieceModel> before = [Straight(1, 0), Straight(2, 1), Straight(3, 2), side];
        var tee = Piece(2, [At(1, 0, 0)], [Towards(1, 0), Towards(1, 2), new PieceEnd(At(1, 1, 0), At(1, 0, 0), PowerAndData, ConnectionRole.None)]);
        List<PieceModel> after = [before[0], tee, before[2], side];

        var diff = Connectivity.Compare(Connectivity.LinksTouching(before, Ids(2)), Connectivity.LinksTouching(after, Ids(2)));

        Assert.False(Connectivity.SameShape(before[1], tee));
        Assert.Equal([Link(2, 4), Link(4, 2)], diff.Added);
        Assert.Empty(diff.Lost);
    }

    [Fact]
    public void A_removed_end_loses_its_links()
    {
        List<PieceModel> before = [Straight(1, 0), Straight(2, 1), Straight(3, 2)];
        var cap = Piece(2, [At(1, 0, 0)], [Towards(1, 0)]);

        var diff = Connectivity.Compare(Connectivity.LinksTouching(before, Ids(2)), Connectivity.LinksTouching([before[0], cap, before[2]], Ids(2)));

        Assert.Empty(diff.Added);
        Assert.Equal([Link(2, 3), Link(3, 2)], diff.Lost);
    }

    [Fact]
    public void Ends_without_a_shared_network_type_do_not_link() =>
        Assert.Empty(Connectivity.LinksTouching([Straight(1, 0, Power), Straight(2, 1, Data)], Ids(1, 2)));

    [Fact]
    public void Gas_and_liquid_pipes_never_link_but_an_any_content_pipe_takes_both()
    {
        var pipe = new NetworkTypes(1);
        Assert.Empty(Connectivity.LinksTouching([Straight(1, 0, pipe, Gas), Straight(2, 1, pipe, Liquid)], Ids(1)));

        var links = Connectivity.LinksTouching([Straight(1, 0, pipe, Gas), Straight(2, 1, pipe, new PipeContent(3, true))], Ids(1));

        Assert.Contains(Link(1, 2), links);
        Assert.DoesNotContain(Link(2, 1), links);
    }

    [Fact]
    public void Same_shape_ignores_order_but_not_content()
    {
        var a = Piece(1, [At(0, 0, 0), At(1, 0, 0)], [Towards(0, -1, Power), Towards(1, 2, Power)], Gas);
        var reordered = Piece(9, [At(1, 0, 0), At(0, 0, 0)], [Towards(1, 2, Power), Towards(0, -1, Power)], new PipeContent(1, false));
        var liquid = a with { Id = Id(9), Content = Option.Some(Liquid) };
        var longer = a with { Id = Id(9), Cells = [At(0, 0, 0), At(1, 0, 0), At(2, 0, 0)] };

        Assert.True(Connectivity.SameShape(a, reordered));
        Assert.False(Connectivity.SameShape(a, liquid));
        Assert.False(Connectivity.SameShape(a, longer));
    }

    [Fact]
    public void A_long_straight_links_only_at_its_tips()
    {
        var links = Connectivity.LinksTouching([Straight(1, 0), Long(2, 1, 3), Straight(3, 4)], Ids(2));

        Assert.Equal(4, links.Count);
        Assert.Contains(Link(1, 2), links);
        Assert.Contains(Link(2, 1), links);
        Assert.Contains(Link(2, 3), links);
        Assert.Contains(Link(3, 2), links);
    }

    [Fact]
    public void Long_straight_twins_next_to_each_other_change_no_link()
    {
        List<PieceModel> before = [Straight(1, -1), Long(2, 0, 5), Long(3, 5, 3), Straight(4, 8)];
        var five = Long(2, 0, 5);
        var three = Long(3, 5, 3);
        var linksBefore = Connectivity.LinksTouching(before, Ids(2, 3));

        Assert.True(Connectivity.SameShape(before[1], five));
        Assert.True(Connectivity.SameShape(before[2], three));
        Assert.Contains(Link(2, 3), linksBefore);
        Assert.Contains(Link(3, 2), linksBefore);
        Assert.Equal(6, linksBefore.Count);
        Assert.True(Connectivity.Compare(linksBefore, Connectivity.LinksTouching([before[0], five, three, before[3]], Ids(2, 3))).Same);
    }

    [Fact]
    public void A_straight_of_another_length_is_no_twin()
    {
        Assert.False(Connectivity.SameShape(Long(2, 0, 3), Long(2, 0, 5)));
        Assert.False(Connectivity.SameShape(Long(2, 0, 3), Straight(2, 1)));
    }

    [Fact]
    public void A_tee_between_two_opposite_neighbours_keeps_only_the_ends_along_them()
    {
        var connected = Connectivity.ConnectedEnds(Tee(), [StraightX(2, At(1, 0, 0)), StraightX(3, At(-1, 0, 0))]);
        var wanted = Tee() with { Ends = connected };

        Assert.Equal(["+x", "-x"], Directions(connected));
        Assert.True(Connectivity.SameShape(wanted, StraightX(1, Origin)));
        Assert.False(Connectivity.SameShape(wanted, StraightZ(1, Origin)));
        Assert.False(Connectivity.SameShape(wanted, Piece(1, Origin, End(Origin, 1, 0, 0), End(Origin, 0, 0, 1))));
    }

    [Fact]
    public void The_piece_with_only_the_connected_ends_keeps_every_link()
    {
        var tee = Tee();
        var left = StraightX(2, At(1, 0, 0));
        var right = StraightX(3, At(-1, 0, 0));
        var straight = tee with { Ends = Connectivity.ConnectedEnds(tee, [left, right]) };

        var before = Connectivity.LinksTouching([tee, left, right], Ids(1));
        var after = Connectivity.LinksTouching([straight, left, right], Ids(1));

        Assert.Equal(4, before.Count);
        Assert.True(Connectivity.Compare(before, after).Same);
    }

    [Fact]
    public void A_cable_passing_across_an_end_is_not_connected_to_it() =>
        Assert.Equal(
            ["+x", "-x"],
            Directions(Connectivity.ConnectedEnds(Tee(), [StraightX(2, At(1, 0, 0)), StraightX(3, At(-1, 0, 0)), StraightX(4, At(0, 0, 1))])));

    [Fact]
    public void A_device_end_facing_the_piece_connects_its_end_through_a_shared_network_type()
    {
        var deviceCell = At(0, 0, 1);
        var dataDevice = Piece(9, deviceCell, End(deviceCell, 0, 0, -1, Data));
        var otherDevice = Piece(9, deviceCell, End(deviceCell, 0, 0, -1, new NetworkTypes(8)));

        Assert.Equal(["+z"], Directions(Connectivity.ConnectedEnds(Tee(), [dataDevice])));
        Assert.Empty(Connectivity.ConnectedEnds(Tee(), [otherDevice]));
    }

    [Fact]
    public void Ends_without_a_shared_network_type_are_open()
    {
        var dataOnly = Piece(1, Origin, End(Origin, 1, 0, 0, Data), End(Origin, -1, 0, 0, Data));
        var next = At(1, 0, 0);
        var powerOnly = Piece(2, next, End(next, 1, 0, 0, Power), End(next, -1, 0, 0, Power));

        Assert.Empty(Connectivity.ConnectedEnds(dataOnly, [powerOnly]));
    }

    [Fact]
    public void A_stub_at_the_end_of_a_run_has_one_connected_end() =>
        Assert.Equal(["-x"], Directions(Connectivity.ConnectedEnds(StraightX(1, Origin), [StraightX(2, At(-1, 0, 0))])));

    [Fact]
    public void The_singles_of_a_long_straight_are_the_straights_along_it_not_across_it()
    {
        var middle = At(0, 0, 1);
        var single = Piece(1, middle, End(middle, 0, 0, -1), End(middle, 0, 0, 1));

        Assert.True(Connectivity.SameShape(single, StraightZ(1, middle)));
        Assert.False(Connectivity.SameShape(single, StraightX(1, middle)));
    }

    [Fact]
    public void Splitting_a_long_straight_into_singles_keeps_every_link_out_of_the_run_and_adds_none()
    {
        var run = Long(2, 0, 5);
        var left = Straight(1, -1);
        var right = Straight(3, 5);
        var beside = Piece(4, At(2, 0, 1), End(At(2, 0, 1), -1, 0, 0), End(At(2, 0, 1), 1, 0, 0));
        List<PieceModel> after = [left, right, beside];
        for (var x = 0; x < 5; x++)
            after.Add(Straight(2, x));

        var linksBefore = Connectivity.LinksTouching([run, left, right, beside], Ids(2));

        Assert.Equal(4, linksBefore.Count);
        Assert.True(Connectivity.Compare(linksBefore, Connectivity.LinksTouching(after, Ids(2))).Same);
    }

    [Fact]
    public void Links_after_a_removal_are_the_links_before_less_the_removed_pieces_own()
    {
        var trunk = new List<PieceModel> { Straight(1, 0), Piece(2, [At(1, 0, 0)], [Towards(1, 0), Towards(1, 2), End(At(1, 0, 0), 0, 0, 1)]), Straight(3, 2) };
        var stub = Piece(4, At(1, 0, 1), End(At(1, 0, 1), 0, 0, -1));
        List<PieceModel> all = [.. trunk, stub];
        var gone = Ids(4);

        var expected = Connectivity.Without(Connectivity.LinksTouching(all, Ids(1, 2, 3, 4)), gone);
        var after = Connectivity.LinksTouching(trunk, Ids(1, 2, 3, 4));

        Assert.True(Connectivity.Compare(expected, after).Same);
        Assert.Equal(4, after.Count);
    }

    [Fact]
    public void Merging_singles_into_one_long_keeps_every_link_out_of_the_run()
    {
        List<PieceModel> singles = [Straight(1, 0), Straight(2, 1), Straight(3, 2)];
        var west = Straight(8, -1);
        var east = Straight(9, 3);
        var group = new Dictionary<ReferenceId, ReferenceId> { [Id(1)] = Id(1), [Id(2)] = Id(1), [Id(3)] = Id(1) };

        var expected = Connectivity.Grouped(Connectivity.LinksTouching([.. singles, west, east], Ids(1, 2, 3)), group);
        var after = Connectivity.LinksTouching([Long(1, 0, 3), west, east], Ids(1, 2, 3));

        Assert.Equal(4, expected.Count);
        Assert.True(Connectivity.Compare(expected, after).Same);
    }

    [Fact]
    public void Links_touching_in_steps_finds_the_same_links_one_model_per_step()
    {
        List<PieceModel> run = [Straight(1, 0), Straight(2, 1), Straight(3, 2)];
        var links = new HashSet<Link>();
        var work = new StepsWork(Connectivity.LinksTouchingSteps(run, Ids(2), links));

        var steps = 0;
        while (work.Step() == Lattice.Core.Slices.StepOutcome.More)
            steps++;

        Assert.Equal(1 + run.Count, steps);
        Assert.True(Connectivity.Compare(Connectivity.LinksTouching(run, Ids(2)), links).Same);
    }
}
