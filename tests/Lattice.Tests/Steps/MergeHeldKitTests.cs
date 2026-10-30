using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Tests.Board;
using Lattice.Tests.Catalogue;
using Lattice.Tests.Matching;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Steps.StepsBench;

namespace Lattice.Tests.Steps;

/// <summary>
/// A merge holds the kit stack in the active hand and the merge tool in the other (MultiMergeConstructor.Construct):
/// the step names the stack as placing the new piece uses it (the game's placement reads the stack in hand against that
/// piece's entry quantity) and uses up the cost difference from it, nothing when free. The bill counts the stack as held,
/// not used up. The walk covers every merging kit of the game's own catalogue (the fixture export) and every pair whose
/// ends the game would join: the old piece's ends, in its turn, among the new one's.
/// </summary>
public sealed class MergeHeldKitTests
{
    private const string ModCable =
        "piece ModCableStraight kit=ItemModCoil option=0 grid=small\n" +
        "  step material=ItemModCoil*1 exit=ItemModSnips air=no\n" +
        "  end 0 0 0 z+ mod:ModGrid:1024 reach=400\n" +
        "  end 0 0 0 z- mod:ModGrid:1024 reach=340\n" +
        "  cell 0 0 0 cable\n" +
        "  collision 2\n" +
        "piece ModCableJunction kit=ItemModCoil option=1 grid=small\n" +
        "  step material=ItemModCoil*2 exit=ItemModSnips air=no\n" +
        "  end 0 0 0 x- mod:ModGrid:1024 reach=340\n" +
        "  end 0 0 0 z+ mod:ModGrid:1024 reach=400\n" +
        "  end 0 0 0 z- mod:ModGrid:1024 reach=340\n" +
        "  cell 0 0 0 cable\n" +
        "  collision 2\n" +
        "piece ModCableCross kit=ItemModCoil option=2 grid=small\n" +
        "  step material=ItemModCoil*2 exit=ItemModSnips air=no\n" +
        "  end 0 0 0 x- mod:ModGrid:1024 reach=340\n" +
        "  end 0 0 0 x+ mod:ModGrid:1024 reach=340\n" +
        "  end 0 0 0 z+ mod:ModGrid:1024 reach=400\n" +
        "  end 0 0 0 z- mod:ModGrid:1024 reach=340\n" +
        "  cell 0 0 0 cable\n" +
        "  collision 2\n";

    private static readonly string[] MergingKits =
    [
        "ItemCableCoil", "ItemCableCoilHeavy", "ItemCableCoilSuperHeavy",
        "ItemKitPipe", "ItemKitPipeLiquid", "ItemKitInsulatedPipe", "ItemKitInsulatedLiquidPipe",
    ];

    private static StepsBench Mod() => new(Merging("ItemModSnips", Pieces(ModCable)));

    private static (Ghost Ghost, StructureInfo Old) Swap(StepsBench bench, string from, string to, Cell at)
    {
        var old = bench.W.Structure(from, at);
        bench.W.World.Load(old);
        return (bench.W.Place(new Intent.Swap(old.Ref, bench.W.Placement(to, at), Option<Finish>.None)), old);
    }

    [Fact]
    public void A_free_merge_holds_the_new_pieces_kit_stack_and_uses_none_of_it()
    {
        var bench = Mod();
        var (ghost, old) = Swap(bench, "ModCableJunction", "ModCableCross", TestWorld.Small);
        bench.Run();

        var plan = bench.Steps(ghost.Id);
        var bill = bench.Bill(TestBoard.Ids(ghost.Id));

        var cross = bench.Catalogue.Named("ModCableCross").Key;
        Assert.Equal(
            ValueList.From<Step>([new Step.Merge(old.Ref, cross, Of("ItemModCoil", 2), Item("ItemModSnips"), Option<ItemQuantity>.None)]),
            plan.Steps);
        Assert.Empty(bill.Needed);
        Assert.Equal(ValueList.From([Of("ItemModCoil", 2)]), bill.Held);
        Assert.Equal(ValueSet.Of(Item("ItemModSnips")), bill.Tools);
    }

    [Fact]
    public void A_dearer_merge_holds_the_new_pieces_kit_stack_and_uses_up_the_difference()
    {
        var bench = Mod();
        var (ghost, old) = Swap(bench, "ModCableStraight", "ModCableCross", TestWorld.Small);
        bench.Run();

        var plan = bench.Steps(ghost.Id);
        var bill = bench.Bill(TestBoard.Ids(ghost.Id));

        var cross = bench.Catalogue.Named("ModCableCross").Key;
        Assert.Equal(
            ValueList.From<Step>([new Step.Merge(old.Ref, cross, Of("ItemModCoil", 2), Item("ItemModSnips"), Some("ItemModCoil", 1))]),
            plan.Steps);
        Assert.Equal(ValueList.From([Of("ItemModCoil", 1)]), bill.Needed);
        Assert.Equal(ValueList.From([Of("ItemModCoil", 1)]), bill.Held);
    }

    [Fact]
    public void A_bill_holds_the_most_one_merge_gets_back_not_the_sum()
    {
        var bench = Mod();
        var (free, _) = Swap(bench, "ModCableJunction", "ModCableCross", TestWorld.Small);
        var (other, _) = Swap(bench, "ModCableJunction", "ModCableCross", TestWorld.Small with { X = TestWorld.Small.X + 10 });
        var (dearer, _) = Swap(bench, "ModCableStraight", "ModCableCross", TestWorld.Small with { X = TestWorld.Small.X + 20 });
        bench.Run();

        var bill = bench.Bill(TestBoard.Ids(free.Id, other.Id, dearer.Id));

        Assert.Equal(ValueList.From([Of("ItemModCoil", 1)]), bill.Needed);
        Assert.Equal(ValueList.From([Of("ItemModCoil", 2)]), bill.Held);
    }

    [Fact]
    public void A_bill_with_no_merge_holds_nothing()
    {
        var bench = Mod();
        var ghost = bench.W.Build("ModCableCross", TestWorld.Small);
        bench.Run();

        Assert.Empty(bench.Bill(TestBoard.Ids(ghost.Id)).Held);
    }

    [GameCatalogueFact]
    public void Every_merge_pair_of_every_merging_kit_in_the_game_catalogue_names_its_held_stack_tool_and_cost()
    {
        var catalogue = GameCatalogue.Load();
        var shapes = new Shapes(catalogue, new CubeGrid());
        var families = catalogue.Pieces
            .Where(static piece => piece.Facts.Merge.IsSome && piece.Facts.Kits.Count > 0)
            .GroupBy(static piece => piece.Facts.Kits[0].Kit)
            .ToList();
        Assert.Equal(MergingKits.Select(PrefabHash.Kit).ToHashSet(), families.Select(static family => family.Key).ToHashSet());

        foreach (var family in families)
        {
            var bench = Over(catalogue);
            var cases = new List<(GhostId Ghost, StructureInfo Old, NamedPiece Becomes, int Was)>();
            foreach (var was in family)
            foreach (var becomes in family)
            {
                if (was.Name == becomes.Name || !Joins(shapes, was, becomes).TryGet(out var turn))
                    continue;
                var at = new Cell(100 + 10 * cases.Count, 100, 100);
                var old = bench.W.Structure(was.Name, at);
                bench.W.World.Load(old);
                var ghost = bench.W.Place(new Intent.Swap(old.Ref, bench.W.Placement(becomes.Name, at, turn), Option<Finish>.None));
                cases.Add((ghost.Id, old, becomes, Cost(was).Count));
            }

            bench.Run();
            var kit = family.First().Name;
            var spent = 0;
            var kept = 0;
            var free = 0;
            foreach (var (ghost, old, becomes, was) in cases)
            {
                var cost = Cost(becomes);
                var merge = Assert.IsType<Step.Merge>(bench.Steps(ghost).Steps[0]);
                Assert.Equal(old.Ref, merge.Into);
                Assert.Equal(becomes.Facts.Key, merge.Becomes);
                Assert.Equal(cost, merge.Held);
                Assert.Equal(Present(becomes.Facts.Merge).OffhandTool, merge.OffhandTool);
                Assert.True(cost.Count >= was, $"{becomes.Name} costs less than the piece it merges into");
                if (cost.Count == was)
                {
                    Assert.True(merge.Extra.IsNone, becomes.Name);
                    free++;
                    kept = System.Math.Max(kept, cost.Count);
                }
                else
                {
                    Assert.Equal(Option.Some(TestKitIds.Must(ItemQuantity.Create(cost.Item, cost.Count - was))), merge.Extra);
                    kept = System.Math.Max(kept, was);
                }

                spent += cost.Count - was;
            }

            Assert.True(free > 0 && free < cases.Count, $"{kit}: {free} free of {cases.Count}");
            var bill = bench.Bill(TestBoard.Ids([.. cases.Select(static c => c.Ghost)]));
            var item = Cost(family.First()).Item;
            Assert.Equal(ValueList.From([TestKitIds.Must(ItemQuantity.Create(item, spent))]), bill.Needed);
            Assert.Equal(ValueList.From([TestKitIds.Must(ItemQuantity.Create(item, kept))]), bill.Held);
        }
    }

    private static ItemQuantity Cost(NamedPiece piece) => Present(piece.Facts.Steps[0].Material);

    private static T Present<T>(Option<T> value) where T : notnull => value.TryGet(out var some) ? some : throw new System.InvalidOperationException("none");

    // The first turn of the new piece whose ends hold every end of the old one in its own turn.
    private static Option<Turn> Joins(Shapes shapes, NamedPiece was, NamedPiece becomes)
    {
        var at = new Cell(0, 0, 0);
        var before = TestKitIds.Must(shapes.Of(new Placement(was.Facts.Key, at, default)));
        for (var i = 0; i < Turn.Count; i++)
        {
            var turn = TestKitIds.Must(Turn.Create(i));
            var after = TestKitIds.Must(shapes.Of(new Placement(becomes.Facts.Key, at, turn)));
            if (before.Ends.All(end => after.Ends.Any(other => other.Cell == end.Cell && other.Facing == end.Facing)))
                return Option.Some(turn);
        }

        return Option<Turn>.None;
    }
}
