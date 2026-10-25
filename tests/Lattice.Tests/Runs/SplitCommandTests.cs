using System.Linq;
using Lattice.Api;
using Lattice.Core.Runs;
using Lattice.Core.Selectors;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Preview;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

/// <summary>
/// <see cref="Command.PlanSplit"/>: long straights planned as single pieces, a ghost one replaced at once by ghosts that
/// keep what it carried, a built one planned as a run joining it plans it. The hand catalogue's cable 10- and 5-longs.
/// </summary>
public sealed class SplitCommandTests
{
    private static readonly Maker Splitter = TestKitIds.Must(Maker.Create(TestKitIds.Mod, "splitter"));
    private static readonly GroupName Trunk = TestKitIds.Must(GroupName.Create("Trunk"));
    private static readonly AttachmentKey Tag = TestKitIds.Must(AttachmentKey.Create(TestKitIds.Mod, "tag"));
    private static readonly Label Name = TestKitIds.Must(Label.Create("main"));
    private static readonly Tint Red = new(200, 10, 10, 255);
    private static int _ids;

    private static PreviewBench Bench()
    {
        var bench = new PreviewBench();
        Assert.True(bench.Expanders.Register(new SplitExpander(bench.Runs.Catalogue, bench.Runs.Grid).Descriptor).IsOk);
        bench.Board.InstallSelectors(new GhostSelectors(bench.Shapes, bench.Runs.Grid, bench.Mirror, bench.Runs.World));
        return bench;
    }

    private static Outcome Submit(PreviewBench bench, Command command) =>
        Assert.IsType<Submission.Answered>(bench.Commands.Submit(new CommandId(new System.Guid(++_ids, 3, 3, new byte[8])), command, TestBoard.Alice)).Outcome;

    private static Command.PlanSplit Split(Selector which) => new(new SplitTargets.Ghosts(which), Option<PlanId>.None, Splitter);

    private static Command.PlanSplit Split(StructureSelector which) => new(new SplitTargets.Structures(which), Option<PlanId>.None, Splitter);

    // A ghost 10-long cable along z from At(0, 0, 9) down to At(0, 0, 0), in its own plan, with a label, tint, group and data.
    private static (Ghost Long, PlanId Plan) LongGhost(PreviewBench bench)
    {
        var plan = TestBoard.Applied(bench.Runs.Boards.Apply(new Command.CreatePlan(new PlanDraft(
            TestKitIds.Must(PlanName.Create("Trunk")), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty)))).PlansOpened[0].Id;
        var draft = new GhostDraft(
            new Intent.Build(new Placement(bench.Runs.Catalogue.Named("StructureCableStraight10").Key, At(0, 0, 9), default), Option<Finish>.None),
            Option.Some(plan), TestBoard.TestMaker, new Visibility.Everyone(), Option.Some(Name), Option.Some(Red),
            ValueSet.Of(Trunk), ValueMap.Of(Tag, TestKitIds.Must(Attachment.Create("kept"))));
        return (bench.Runs.Boards.Placed(draft).Added[0], plan);
    }

    private static string PieceOf(PreviewBench bench, Ghost ghost) => bench.Runs.NameOf(((Intent.Build)ghost.Intent).Target.Piece);

    [Fact]
    public void A_ghost_ten_long_becomes_ten_single_ghosts_that_keep_what_it_carried_and_one_revert_restores_it()
    {
        var bench = Bench();
        var (longGhost, plan) = LongGhost(bench);

        var split = TestBoard.Applied(Submit(bench, Split(TestBoard.Ids(longGhost.Id))));

        Assert.Equal(10, split.Added.Count);
        Assert.Equal(longGhost.Id, Assert.Single(split.Removed).Ghost.Id);
        Assert.All(split.Added, g =>
        {
            Assert.Equal("StructureCableStraight", PieceOf(bench, g));
            Assert.Equal(plan, g.Plan);
            Assert.Equal(Option.Some(Name), g.Label);
            Assert.Equal(Option.Some(Red), g.Tint);
            Assert.Equal(ValueSet.Of(Trunk), g.Groups);
            Assert.Equal("kept", g.Attached[Tag].Value);
        });
        Assert.Equal(Enumerable.Range(0, 10).Select(z => At(0, 0, z)).ToHashSet(), split.Added.Select(g => ((Intent.Build)g.Intent).Target.At).ToHashSet());

        TestBoard.Applied(Submit(bench, new Command.Revert(split.Id)));

        var back = Assert.Single(bench.Board.Index.Ghosts.Values).Ghost;
        Assert.Equal("StructureCableStraight10", PieceOf(bench, back));
    }

    [Fact]
    public void A_built_five_long_is_planned_as_a_run_joining_it_plans_it()
    {
        var bench = Bench();
        var built = bench.Runs.Real("StructureCableStraight5", At(3, 0, 4));

        var split = TestBoard.Applied(Submit(bench, Split(new StructureSelector.Refs(ValueList.Of(built)))));

        var removal = Assert.Single(split.Added, g => g.Intent is Intent.Remove);
        Assert.Equal(built, ((Intent.Remove)removal.Intent).Target);
        var singles = split.Added.Where(g => g.Intent is Intent.Build).ToList();
        Assert.Equal(5, singles.Count);
        Assert.All(singles, g => Assert.Equal("StructureCableStraight", PieceOf(bench, g)));
        Assert.All(split.Added, g => Assert.Equal(Splitter, g.MadeBy));
    }

    [Fact]
    public void A_box_with_no_long_straight_is_refused_nothing_to_split()
    {
        var bench = Bench();
        bench.Runs.Ghost("StructureCableStraight", At(0, 0, 0));
        var corner = bench.Runs.Real("StructureCableCorner", At(1, 0, 0));

        var ghosts = TestBoard.Refused(Submit(bench, Split(new Selector.Box(At(-1, -1, -1), At(2, 1, 1)))));
        var built = TestBoard.Refused(Submit(bench, Split(new StructureSelector.Refs(ValueList.Of(corner)))));

        Assert.All(new[] { ghosts, built }, refusal =>
        {
            Assert.Equal(ReasonCodes.InvalidValue, refusal.Why.Code);
            Assert.Equal("nothing-to-split", refusal.Why.Facts["problem"]);
        });
    }

    [Fact]
    public void A_mixed_box_splits_only_the_long_straights()
    {
        var bench = Bench();
        var (longGhost, _) = LongGhost(bench);
        bench.Runs.Ghost("StructureCableCorner", At(2, 0, 0));
        bench.Runs.Ghost("StructureCableStraight", At(3, 0, 0));

        var split = TestBoard.Applied(Submit(bench, Split(new Selector.Box(At(-1, -1, -1), At(4, 1, 10)))));

        Assert.Equal(longGhost.Id, Assert.Single(split.Removed).Ghost.Id);
        Assert.Equal(10, split.Added.Count);
        Assert.Equal(12, bench.Board.Index.Ghosts.Count);
    }

    [Fact]
    public void The_preview_of_a_split_is_the_change_its_expansion_makes()
    {
        var bench = Bench();
        LongGhost(bench);
        bench.Start();
        var command = Split(new Selector.Box(At(-1, -1, -1), At(1, 1, 10)));

        var preview = bench.Ok(command);
        var applied = TestBoard.Applied(Submit(bench, command));

        Assert.Equal(applied.Added.Select(g => g.Intent), preview.WouldChange.Added.Select(g => g.Intent));
        Assert.Equal(applied.Removed.Select(r => r.Ghost.Id), preview.WouldChange.Removed.Select(r => r.Ghost.Id));
        Assert.True(preview.Run.IsNone);
    }
}
