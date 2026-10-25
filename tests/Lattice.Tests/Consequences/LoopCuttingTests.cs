using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Consequences;
using static Lattice.Tests.Consequences.Models;

namespace Lattice.Tests.Consequences;

/// <summary>Finding and breaking loops, rings included.</summary>
public sealed class LoopCuttingTests
{
    private const long Device = 900;

    private static readonly Reason DeviceMounted = Reason.Of(ReasonCodes.NotAllowed, RuleId.Contract);

    /// <summary>
    /// A device's power port joins cell (0,0,0) and its data port cell (0,0,2), both towards +x. The power route ran
    /// from the power port through the data port cell (a 4-way there) and on to the network (trunk at x = -3); the data
    /// route, planned separately, ran from the data port cell as a parallel run to the same trunk, closing a loop.
    /// </summary>
    private static (List<PieceModel> Pieces, PieceModel Device) Incident()
    {
        List<PieceModel> pieces =
        [
            Run(1, At(0, 0, 0), "+x", "+z"),
            Run(2, At(0, 0, 1), "-z", "+z"),
            Run(3, At(0, 0, 2), "-z", "+x", "-x", "+z"),
            Run(4, At(-1, 0, 2), "+x", "-x"),
            Run(5, At(-2, 0, 2), "+x", "-x"),
            Run(20, At(-3, 0, 0), "-z", "+z"),
            Run(21, At(-3, 0, 1), "-z", "+z"),
            Run(22, At(-3, 0, 2), "-z", "+z", "+x"),
            Run(23, At(-3, 0, 3), "-z", "+z", "+x"),
            Run(24, At(-3, 0, 4), "-z", "+z"),
            Run(10, At(0, 0, 3), "-z", "-x"),
            Run(11, At(-1, 0, 3), "+x", "-x"),
            Run(12, At(-2, 0, 3), "+x", "-x"),
        ];
        var device = Piece(
            Device,
            [At(1, 0, 0), At(1, 0, 1), At(1, 0, 2)],
            [DevicePort(At(0, 0, 0), At(1, 0, 0)), DevicePort(At(0, 0, 2), At(1, 0, 2))]);
        return (pieces, device);
    }

    private static PieceEnd DevicePort(Cell into, Cell from) => new(into, from, PowerAndData, ConnectionRole.None);

    private static LoopResult Find(List<PieceModel> pieces, PieceModel device, HashSet<ReferenceId>? keep = null, Dictionary<ReferenceId, Reason>? blocked = null) =>
        LoopCutting.Find(pieces, [device], Ids(Device), blocked ?? [], keep ?? []);

    private static List<ReferenceId> List(params long[] ids) => IdOrder.Sorted(Ids(ids));

    [Fact]
    public void The_incident_loop_is_found_and_broken_by_the_shorter_parallel_run()
    {
        var (pieces, device) = Incident();
        var result = Find(pieces, device);

        var loop = Assert.Single(result.Loops);
        Assert.False(loop.Spared);
        Assert.True(loop.Unbroken.IsNone);
        Assert.Equal(List(3, 4, 5, 10, 11, 12, 22, 23), loop.Pieces);
        Assert.Equal(List(4, 5), result.Cut);
    }

    [Fact]
    public void After_the_cut_every_piece_and_port_is_still_joined()
    {
        var (pieces, device) = Incident();
        var cut = new HashSet<ReferenceId>(Find(pieces, device).Cut);
        var left = pieces.FindAll(piece => !cut.Contains(piece.Id));

        var reached = Ids(20);
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var a in left)
                foreach (var b in left)
                    if (reached.Contains(a.Id) && !reached.Contains(b.Id) && (Connectivity.Links(a, b) || Connectivity.Links(b, a)))
                    {
                        reached.Add(b.Id);
                        grew = true;
                    }
        }

        Assert.Equal(left.Count, reached.Count);
        Assert.True(Connectivity.Links(device, left.Find(piece => piece.Id == Id(1))!));
        Assert.True(Connectivity.Links(device, left.Find(piece => piece.Id == Id(3))!));
    }

    [Fact]
    public void A_keep_id_spares_the_whole_loop()
    {
        var (pieces, device) = Incident();
        var result = Find(pieces, device, Ids(11));

        Assert.True(Assert.Single(result.Loops).Spared);
        Assert.Empty(result.Cut);
    }

    [Fact]
    public void A_blocked_piece_moves_the_cut_to_the_other_chain()
    {
        var (pieces, device) = Incident();

        Assert.Equal(List(10, 11, 12), Find(pieces, device, blocked: new() { [Id(4)] = DeviceMounted }).Cut);
    }

    [Fact]
    public void Pieces_linked_to_a_device_are_never_cut()
    {
        List<PieceModel> pieces =
        [
            Run(1, At(0, 0, 0), "+x", "+z"),
            Run(2, At(1, 0, 0), "-x", "+z"),
            Run(3, At(0, 0, 1), "-z", "+x", "-x"),
            Run(4, At(1, 0, 1), "-z", "-x", "+x"),
        ];
        var device = Piece(Device, [At(-1, 0, 1)], [DevicePort(At(0, 0, 1), At(-1, 0, 1))]);

        var loop = Assert.Single(Find(pieces, device).Loops);

        Assert.DoesNotContain(Id(3), loop.Cut);
        Assert.Equal(List(1, 2, 4), loop.Cut);
    }

    [Fact]
    public void A_loop_of_pieces_that_may_not_go_stays_unbroken()
    {
        List<PieceModel> pieces =
        [
            Run(1, At(0, 0, 0), "+x", "+z"),
            Run(2, At(1, 0, 0), "-x", "+z"),
            Run(3, At(0, 0, 1), "-z", "+x"),
            Run(4, At(1, 0, 1), "-z", "-x", "+x"),
        ];
        var device = Piece(Device, [At(2, 0, 1)], [DevicePort(At(1, 0, 1), At(2, 0, 1))]);
        var blocked = new Dictionary<ReferenceId, Reason> { [Id(1)] = DeviceMounted, [Id(2)] = DeviceMounted, [Id(3)] = DeviceMounted };

        var loop = Assert.Single(Find(pieces, device, blocked: blocked).Loops);

        Assert.Empty(loop.Cut);
        Assert.True(loop.Unbroken.TryGet(out var why));
        Assert.Equal(ReasonCodes.NoRemovableChain, why.Code);
    }

    [Fact]
    public void A_tree_has_no_loops()
    {
        List<PieceModel> pieces =
        [
            Run(1, At(0, 0, 0), "+x", "-x", "+z"),
            Run(2, At(1, 0, 0), "-x", "+x"),
            Run(3, At(0, 0, 1), "-z", "+z"),
        ];

        Assert.Empty(Find(pieces, Piece(Device, [At(9, 0, 9)], [])).Loops);
    }

    [Fact]
    public void An_isolated_ring_is_found_and_cut_once()
    {
        List<PieceModel> ring =
        [
            Run(1, At(0, 0, 0), "+x", "+z"),
            Run(2, At(1, 0, 0), "-x", "+z"),
            Run(3, At(0, 0, 1), "-z", "+x"),
            Run(4, At(1, 0, 1), "-z", "-x"),
        ];

        var loop = Assert.Single(LoopCutting.Find(ring, [], [], new Dictionary<ReferenceId, Reason>(), []).Loops);

        Assert.Single(loop.Cut);
        Assert.True(loop.Unbroken.IsNone);
    }

    [Fact]
    public void A_rings_loop_lists_every_piece_of_the_ring()
    {
        List<PieceModel> ring =
        [
            Run(1, At(0, 0, 0), "+x", "+z"),
            Run(2, At(1, 0, 0), "-x", "+z"),
            Run(3, At(1, 0, 1), "-x", "-z"),
            Run(4, At(0, 0, 1), "+x", "-z"),
        ];

        var loop = Assert.Single(LoopCutting.Find(ring, [], [], new Dictionary<ReferenceId, Reason>(), []).Loops);

        Assert.Equal(List(1, 2, 3, 4), loop.Pieces);
        Assert.Single(loop.Cut);
    }
}
