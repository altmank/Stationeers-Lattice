using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.TestKit.Fixtures;
using Lattice.Tests.Catalogue;
using Xunit.Abstractions;

namespace Lattice.Tests.Matching;

/// <summary>
/// A built piece completes its ghost in any turn that looks the same: the same spots and the same ends by cell,
/// facing, kinds and role. A T junction turned end for end, a cross or a straight spun about its own line all
/// complete; a turn that moves an end, or a chute junction turned so its input and output swap, stays stale. Over the
/// catalogue the game wrote.
/// </summary>
public sealed class LookAlikeTurnsTests(ITestOutputHelper output)
{
    private static IEnumerable<Turn> Turns() => Enumerable.Range(0, Turn.Count).Select(TestWorld.TurnOf);

    private static TestWorld Started()
    {
        var w = new TestWorld(Fixtures.Catalogue());
        w.Run();
        return w;
    }

    private static PrefabKey Key(TestWorld w, string piece) => w.T.Catalogue.Named(piece).Key;

    /// <summary>The turns a structure of the piece may stand in to complete a ghost of it in the unturned pose.</summary>
    private static List<Turn> Alike(TestWorld w, string piece) =>
        Turns().Where(turn => w.TurnFree.LookAlike(Key(w, piece), Turn.None, turn)).ToList();

    /// <summary>The turn that sends both directions where asked.</summary>
    private static Turn Sending(TestWorld w, Direction from, Direction to, Direction keeps) =>
        Turns().Single(turn => w.T.Grid.Apply(turn, from) == to && w.T.Grid.Apply(turn, keeps) == keeps);

    [GameCatalogueFact]
    public void A_t_junction_has_two_turns_that_look_alike_and_a_cross_and_a_straight_eight()
    {
        var w = Started();

        Assert.Equal(2, Alike(w, "StructureCableJunction").Count);
        Assert.Equal(8, Alike(w, "StructureCableJunction4").Count);
        Assert.Equal(8, Alike(w, "StructureCableStraight").Count);
        Assert.Equal(2, Alike(w, "StructureCableJunctionH").Count);
        Assert.Single(Alike(w, "StructureChuteJunction"));
        Assert.Equal(8, Alike(w, "StructureChuteStraight").Count);
    }

    [GameCatalogueFact]
    public void A_junction_cross_or_straight_built_in_any_turn_that_looks_alike_completes_its_ghost()
    {
        foreach (var piece in new[] { "StructureCableJunction", "StructureCableJunction4", "StructureCableStraight", "StructureCableJunctionH4", "StructureChuteStraight" })
            foreach (var turn in Alike(Started(), piece))
            {
                var w = Started();
                var ghost = w.Build(piece, TestWorld.Small);
                var done = w.Completions();

                w.World.Register(w.Structure(piece, TestWorld.Small, turn));
                w.T.Events.Flush();

                Assert.True(w.Find(ghost.Id).IsNone, $"{piece} built in turn {turn.Index}");
                Assert.Equal(ghost.Id, Assert.Single(done).Ghost);
            }
    }

    [GameCatalogueFact]
    public void A_junction_turned_so_an_end_moves_is_stale_with_the_turn_as_its_problem()
    {
        var w = Started();
        var ghost = w.Build("StructureCableJunction", TestWorld.Small);
        var moved = Sending(w, Direction.XMinus, Direction.ZMinus, Direction.YPlus);
        Assert.DoesNotContain(moved, Alike(w, "StructureCableJunction"));

        w.World.Register(w.Structure("StructureCableJunction", TestWorld.Small, moved));

        var stale = Assert.IsType<GhostState.Stale>(w.StateOf(ghost.Id));
        Assert.Equal(ReasonCodes.Different, stale.Why.Code);
        Assert.Equal("turn", stale.Why.Facts["problem"]);
    }

    [GameCatalogueFact]
    public void A_chute_junction_turned_so_its_input_and_output_swap_is_stale_though_its_ends_lie_the_same_way()
    {
        var w = Started();
        var ghost = w.Build("StructureChuteJunction", TestWorld.Small);
        var swapped = Sending(w, Direction.XPlus, Direction.XMinus, Direction.YPlus);

        w.World.Register(w.Structure("StructureChuteJunction", TestWorld.Small, swapped));

        Assert.Equal("turn", Assert.IsType<GhostState.Stale>(w.StateOf(ghost.Id)).Why.Facts["problem"]);
    }

    [GameCatalogueFact]
    public void A_junction_standing_in_a_turn_that_looks_alike_at_load_completes_its_ghost()
    {
        var w = new TestWorld(Fixtures.Catalogue());
        var turned = Alike(w, "StructureCableJunction").Single(turn => turn != Turn.None);
        var ghost = w.Build("StructureCableJunction", TestWorld.Small);
        w.World.Load(w.Structure("StructureCableJunction", TestWorld.Small, turned));

        var report = w.Run();

        Assert.Equal(1, report.Done);
        Assert.Equal(0, report.Stale);
        Assert.True(w.Find(ghost.Id).IsNone);
    }

    [GameCatalogueFact]
    public void The_cable_pipe_and_chute_pieces_with_turns_that_look_alike_are_counted()
    {
        var w = Started();
        var families = new[] { PieceFamilies.Cable, PieceFamilies.Pipe, PieceFamilies.Chute };
        var counts = new Dictionary<PieceFamily, (int Pieces, int Alike)>();
        foreach (var entry in w.T.Catalogue.Listing.Pieces)
        {
            if (!w.T.Catalogue.Piece(entry.Key).TryGet(out var facts) || !families.Contains(facts.Family) || w.TurnFree.Has(entry.Key))
                continue;
            var alike = Turns().Any(turn => turn != Turn.None && w.TurnFree.LookAlike(entry.Key, Turn.None, turn));
            var (pieces, withAlike) = counts.TryGetValue(facts.Family, out var seen) ? seen : (0, 0);
            counts[facts.Family] = (pieces + 1, withAlike + (alike ? 1 : 0));
        }

        foreach (var (family, (pieces, alike)) in counts)
            output.WriteLine($"{family.Value}: {alike} of {pieces} pieces have another turn that looks alike");
        Assert.All(families, family => Assert.True(counts.TryGetValue(family, out var c) && c.Alike > 0, family.Value));
    }
}
