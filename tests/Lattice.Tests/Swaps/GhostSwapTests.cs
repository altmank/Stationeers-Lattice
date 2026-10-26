using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Steps;
using Lattice.Core.Swaps;
using Lattice.TestKit;
using Lattice.Tests.Board;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Swaps;

/// <summary>
/// <see cref="GhostEdit.Swap"/>: planned pieces change in place, ids and fields kept, what follows from the piece worked
/// out again; ghosts that cannot change are left as they are; and <see cref="Command.PlanSwap"/> keeps a swap ghost.
/// </summary>
public sealed class GhostSwapTests
{
    private const string Straight = "StructureCableStraight";
    private const string Corner = "StructureCableCorner";
    private const string Tee = "StructureCableJunction";
    private const string Cross = "StructureCableJunction4";
    private const string ThreeLong = "StructureCableStraight3";
    private const string HeavyStraight = "StructureCableStraightH";
    private const string Pipe = "StructurePipeStraight";

    private static readonly Label Name = TestKitIds.Must(Label.Create("trunk"));
    private static readonly Tint Red = new(200, 10, 10, 255);
    private static readonly GroupName Group = TestKitIds.Must(GroupName.Create("Power"));
    private static readonly AttachmentKey Tag = TestKitIds.Must(AttachmentKey.Create(TestKitIds.Mod, "tag"));

    private static PlanId NewPlan(GhostSwapBench bench, string name = "Power") =>
        TestBoard.Applied(bench.Boards.Apply(new Command.CreatePlan(new PlanDraft(
            TestKitIds.Must(PlanName.Create(name)), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty)))).PlansOpened[0].Id;

    private static GhostDraft Carrying(Intent intent, PlanId plan) => new(
        intent, Option.Some(plan), TestBoard.TestMaker, new Visibility.Everyone(), Option.Some(Name), Option.Some(Red),
        ValueSet.Of(Group), ValueMap.Of(Tag, TestKitIds.Must(Attachment.Create("kept"))));

    private static Intent Build(GhostSwapBench bench, string piece, Cell at, Turn turn = default) =>
        new Intent.Build(bench.At(piece, at, turn), Option<Finish>.None);

    private static GhostId Place(GhostSwapBench bench, Intent intent) =>
        Assert.Single(bench.Boards.Placed(TestBoard.Draft(intent)).Added).Id;

    private static GhostId Place(GhostSwapBench bench, string piece, Cell at, Turn turn = default) => Place(bench, Build(bench, piece, at, turn));

    private static IEnumerable<GhostId> ChangedIds(ChangeSet changes) => changes.Changed.Select(static c => c.After.Id);

    // A run along x into a corner, a three-long and a straight along z, ending in a tee, in its own plan with every field set.
    private static (List<GhostId> Run, PlanId Plan) PlannedRun(GhostSwapBench bench)
    {
        var plan = NewPlan(bench);
        var drafts = new[]
        {
            Carrying(Build(bench, Straight, At(-1, 0, -1), bench.AlongX), plan),
            Carrying(Build(bench, Corner, At(0, 0, -1)), plan),
            Carrying(Build(bench, ThreeLong, At(0, 0, 2)), plan),
            Carrying(Build(bench, Straight, At(0, 0, 3)), plan),
            Carrying(Build(bench, Tee, At(0, 0, 4)), plan),
        };
        return (bench.Boards.Placed(drafts).Added.Select(static g => g.Id).ToList(), plan);
    }

    [Fact]
    public void A_planned_cable_run_becomes_heavy_in_place_keeping_ids_plan_and_fields()
    {
        var bench = new GhostSwapBench().Start();
        var (run, plan) = PlannedRun(bench);
        var before = run.ToDictionary(id => id, bench.Now);
        var fits = new SwapShapes(bench.Runs.Catalogue, bench.Preview.Shapes);

        var changes = bench.Applied(bench.Swap(new Selector.InPlan(plan), bench.Heavy));

        Assert.Equal(run.OrderBy(static id => id.Value), ChangedIds(changes).OrderBy(static id => id.Value));
        Assert.Empty(changes.Added);
        Assert.Empty(changes.Removed);
        foreach (var id in run)
        {
            var (was, now) = (before[id], bench.Now(id));
            var (old, heavy) = (GhostSwapBench.PlacementOf(was), GhostSwapBench.PlacementOf(now));
            Assert.Equal(bench.Name(old.Piece) + "H", bench.Name(heavy.Piece));
            Assert.Equal(old.At, heavy.At);
            Assert.Equal(fits.FitOf(old.Piece, old.Turn, heavy.Piece), Option.Some(heavy.Turn));
            Assert.Equal(heavy, bench.ShapeOf(id));
            Assert.Equal(bench.Runs.CellsOf(old), bench.Runs.CellsOf(heavy));
            foreach (var cell in bench.Runs.CellsOf(old))
                Assert.Equal(bench.Runs.WaysOf(old, cell), bench.Runs.WaysOf(heavy, cell));
            Assert.Equal(was with { Intent = now.Intent, Revision = now.Revision }, now);
            Assert.Equal(changes.Revision, now.Revision);
        }
    }

    [Fact]
    public void A_run_selector_swaps_the_run_through_a_cell()
    {
        var bench = new GhostSwapBench().Start();
        var (run, _) = PlannedRun(bench);
        var picked = TestKitIds.Must(bench.View.Select(new Selector.RunThrough(At(0, 0, 3)))).Select(static g => g.Id).ToList();
        Assert.NotEmpty(picked);

        var changes = bench.Applied(bench.Swap(new Selector.RunThrough(At(0, 0, 3)), bench.Heavy));

        Assert.Equal(picked.OrderBy(static id => id.Value), ChangedIds(changes).OrderBy(static id => id.Value));
        Assert.All(picked, id => Assert.EndsWith("H", bench.Name(GhostSwapBench.PlacementOf(bench.Now(id)).Piece)));
    }

    [Fact]
    public void A_rail_that_waits_for_a_planned_frame_still_waits_for_it_after_the_swap()
    {
        var bench = new GhostSwapBench().Start();
        var frame = Place(bench, "StructureFrameIron", SwapBench.Large(0, 0, 0));
        var rails = new[] { Place(bench, "ModRail", new Cell(110, 120, 110)), Place(bench, "ModRail", new Cell(110, 120, 115)) };
        bench.Preview.Settle();
        var waitsBefore = rails.Select(r => Assert.IsType<Finding.WaitsFor>(Verdict(bench, r))).ToList();
        Assert.All(waitsBefore, w => Assert.Contains(frame, w.Others));

        bench.Applied(bench.Swap(TestBoard.Ids(rails), new SwapTo.SameShapeFrom(bench.Kit("ModRailHeavy"))));
        bench.Preview.Settle();

        Assert.All(rails, r => Assert.Equal("ModRailHeavy", bench.Name(GhostSwapBench.PlacementOf(bench.Now(r)).Piece)));
        Assert.Equal(waitsBefore, rails.Select(r => Assert.IsType<Finding.WaitsFor>(Verdict(bench, r))));
    }

    [Fact]
    public void The_bill_steps_and_piece_selectors_follow_the_new_piece()
    {
        var bench = new GhostSwapBench().Start();
        var cable = Place(bench, Straight, At(0, 0, 0));
        var coil = new ItemKey(bench.Kit(Straight).Hash);
        var heavyCoil = new ItemKey(bench.Kit(HeavyStraight).Hash);
        Assert.Contains(BillOf(bench, cable).Needed, q => q.Item == coil);

        bench.Applied(bench.Swap(TestBoard.Ids(cable), bench.Heavy));

        var bill = BillOf(bench, cable);
        Assert.Contains(bill.Needed, q => q.Item == heavyCoil);
        Assert.DoesNotContain(bill.Needed, q => q.Item == coil);
        Assert.Empty(TestKitIds.Must(bench.View.Select(new Selector.OfPiece(bench.Piece(Straight)))));
        Assert.Equal(cable, Assert.Single(TestKitIds.Must(bench.View.Select(new Selector.OfPiece(bench.Piece(HeavyStraight))))).Id);
    }

    [Fact]
    public void A_swap_ghost_takes_another_piece_that_fits_keeping_its_id_and_its_structure()
    {
        var bench = new GhostSwapBench();
        var built = bench.Runs.Real(Straight, At(0, 0, 0), bench.AlongX);
        bench.Start();
        var swap = Place(bench, new Intent.Swap(built, bench.At(HeavyStraight, At(0, 0, 0), bench.AlongX), Option<Finish>.None));

        var changes = bench.Applied(bench.Swap(TestBoard.Ids(swap), new SwapTo.Exactly(bench.Piece("ModArmouredCable"))));

        var now = Assert.IsType<Intent.Swap>(bench.Now(swap).Intent);
        Assert.Equal(swap, Assert.Single(ChangedIds(changes)));
        Assert.Equal(built, now.Old);
        Assert.Equal("ModArmouredCable", bench.Name(now.New.Piece));
        Assert.Equal(bench.Runs.WaysOf(new Placement(built.Piece, built.At, bench.AlongX), built.At), bench.Runs.WaysOf(now.New, built.At));
        Assert.Equal(swap, Assert.Single(bench.View.On(built)).Id);
    }

    [Fact]
    public void Removal_finish_part_built_and_already_heavy_ghosts_are_left_as_they_are()
    {
        var bench = new GhostSwapBench();
        var (removed, finished, started) = (
            bench.Runs.Real(Straight, At(5, 0, 0)), bench.Runs.Real(Straight, At(6, 0, 0)), bench.Runs.Real(Straight, At(7, 0, 0)));
        bench.Start();
        var cable = Place(bench, Straight, At(0, 0, 0));
        var heavy = Place(bench, HeavyStraight, At(1, 0, 0));
        var removal = Place(bench, new Intent.Remove(removed));
        var finish = Place(bench, new Intent.FinishOnly(finished, TestKitIds.Must(Finish.Create(Option.Some(TestKitIds.Must(PaintColour.Create(3))), Option<ThingName>.None, Option<Setting>.None))));
        var partBuilt = Place(bench, Straight, At(7, 0, 0));
        TestKitIds.Must(bench.Board.MarkStarted(partBuilt, started));
        var before = new[] { heavy, removal, finish, partBuilt }.ToDictionary(id => id, bench.Now);

        var changes = bench.Applied(bench.Swap(new Selector.Everything(), bench.Heavy));

        Assert.Equal(cable, Assert.Single(ChangedIds(changes)));
        Assert.All(before, pair => Assert.Equal(pair.Value, bench.Now(pair.Key)));
    }

    [Fact]
    public void A_box_drag_swaps_what_has_a_heavy_shape_and_leaves_a_cross_out()
    {
        var bench = new GhostSwapBench().Start();
        var straight = Place(bench, Straight, At(0, 0, 0));
        var cross = Place(bench, Cross, At(2, 0, 0));

        var changes = bench.Applied(bench.Swap(new Selector.Box(At(-1, -1, -1), At(3, 1, 1)), bench.Heavy));

        Assert.Equal(straight, Assert.Single(ChangedIds(changes)));
        Assert.Equal(Cross, bench.Name(GhostSwapBench.PlacementOf(bench.Now(cross)).Piece));
    }

    [Fact]
    public void A_swap_ghost_that_would_become_its_own_structure_is_left_out()
    {
        var bench = new GhostSwapBench();
        var built = bench.Runs.Real(Straight, At(0, 0, 0), bench.AlongX);
        bench.Start();
        var swap = Place(bench, new Intent.Swap(built, bench.At(HeavyStraight, At(0, 0, 0), bench.AlongX), Option<Finish>.None));
        var heavy = Place(bench, HeavyStraight, At(3, 0, 0));
        var before = bench.Now(swap);

        var changes = bench.Applied(bench.Swap(TestBoard.Ids(swap, heavy), new SwapTo.SameShapeFrom(bench.Kit(Straight))));

        Assert.Equal(heavy, Assert.Single(ChangedIds(changes)));
        Assert.Equal(before, bench.Now(swap));
        var alone = bench.Refused(bench.Swap(TestBoard.Ids(swap), new SwapTo.SameShapeFrom(bench.Kit(Straight))));
        Assert.Equal(ReasonCodes.InvalidValue, alone.Why.Code);
        Assert.Equal("own-structure", alone.Why.Facts["problem"]);
    }

    [Fact]
    public void A_swap_where_nothing_picked_fits_is_refused_with_the_first_reason_and_the_count()
    {
        var bench = new GhostSwapBench().Start();
        var crosses = new[] { Place(bench, Cross, At(0, 0, 0)), Place(bench, Cross, At(3, 0, 0)) };

        var refused = bench.Refused(bench.Swap(TestBoard.Ids(crosses), bench.Heavy));

        Assert.Equal(ReasonCodes.NoPiece, refused.Why.Code);
        Assert.Equal("2", refused.Why.Facts["picked"]);
        Assert.Equal(bench.Piece(Cross).Hash.ToString(), refused.Why.Facts["piece"]);
        Assert.Equal(bench.Kit(HeavyStraight).Hash.ToString(), refused.Why.Facts["kit"]);
        Assert.Equal(crosses[0], Assert.Single(refused.Ghosts));
    }

    [Fact]
    public void A_kit_the_catalogue_does_not_know_is_refused_with_the_catalogue_reason()
    {
        var bench = new GhostSwapBench().Start();
        var cable = Place(bench, Straight, At(0, 0, 0));
        var unknown = new KitKey(12345);
        var catalogueSays = bench.Runs.Catalogue.Kit(unknown).ErrorOf();

        var refused = bench.Refused(bench.Swap(TestBoard.Ids(cable), new SwapTo.SameShapeFrom(unknown)));

        Assert.Equal(catalogueSays.Code, refused.Why.Code);
        Assert.Equal("1", refused.Why.Facts["picked"]);
    }

    [Fact]
    public void Without_swaps_the_edit_is_refused_module_missing()
    {
        var bench = new GhostSwapBench(swaps: false).Start();
        var cable = Place(bench, Straight, At(0, 0, 0));

        var refused = bench.Refused(bench.Swap(TestBoard.Ids(cable), bench.Heavy));

        Assert.Equal(ReasonCodes.ModuleMissing, refused.Why.Code);
        Assert.Equal("swaps", refused.Why.Facts["module"]);
    }

    [Fact]
    public void A_selector_that_picks_nothing_changes_nothing()
    {
        var bench = new GhostSwapBench().Start();

        var changes = bench.Applied(bench.Swap(new Selector.Box(At(0, 0, 0), At(1, 1, 1)), bench.Heavy));

        Assert.Empty(changes.Changed);
    }

    [Fact]
    public void A_new_piece_that_cannot_share_its_cell_with_a_ghost_there_is_refused_naming_both()
    {
        var bench = new GhostSwapBench().Start();
        var pipe = Place(bench, Pipe, At(0, 0, 0));
        var cable = Place(bench, Straight, At(0, 0, 0), bench.AlongX);

        var refused = bench.Refused(bench.Swap(TestBoard.Ids(cable), new SwapTo.Exactly(bench.Piece("ModWideCable"))));

        Assert.Equal(ReasonCodes.CannotShare, refused.Why.Code);
        Assert.Equal(new[] { cable, pipe }, refused.Ghosts);
        Assert.Equal(Straight, bench.Name(GhostSwapBench.PlacementOf(bench.Now(cable)).Piece));
    }

    [Fact]
    public void Revert_puts_every_piece_and_shape_back()
    {
        var bench = new GhostSwapBench().Start();
        var (run, plan) = PlannedRun(bench);
        var before = run.ToDictionary(id => id, bench.Now);
        var swapped = bench.Applied(bench.Swap(new Selector.InPlan(plan), bench.Heavy));

        var reverted = bench.Applied(new Command.Revert(swapped.Id));

        foreach (var id in run)
        {
            Assert.Equal(before[id] with { Revision = reverted.Revision }, bench.Now(id));
            Assert.Equal(GhostSwapBench.PlacementOf(before[id]), bench.ShapeOf(id));
        }

        Assert.Equal(run.Count, TestKitIds.Must(bench.View.Select(new Selector.OfPiece(bench.Piece(Straight)))).Count
            + TestKitIds.Must(bench.View.Select(new Selector.OfPiece(bench.Piece(Corner)))).Count
            + TestKitIds.Must(bench.View.Select(new Selector.OfPiece(bench.Piece(ThreeLong)))).Count
            + TestKitIds.Must(bench.View.Select(new Selector.OfPiece(bench.Piece(Tee)))).Count);
    }

    [Fact]
    public void Revert_of_a_swap_is_refused_when_the_old_piece_cannot_share_its_cell_with_a_ghost_placed_since()
    {
        var bench = new GhostSwapBench().Start();
        var wide = Place(bench, "ModWideCable", At(0, 0, 0), bench.AlongX);
        var swapped = bench.Applied(bench.Swap(TestBoard.Ids(wide), new SwapTo.Exactly(bench.Piece(Straight))));
        var pipe = Place(bench, Pipe, At(0, 0, 0));

        var refused = bench.Refused(new Command.Revert(swapped.Id));

        Assert.Equal(ReasonCodes.CannotShare, refused.Why.Code);
        Assert.Equal(new[] { wide, pipe }, refused.Ghosts);
        Assert.Equal(Straight, bench.Name(bench.ShapeOf(wide).Piece));
    }

    [Fact]
    public void An_unknown_kind_of_swap_is_refused_even_when_nothing_is_picked()
    {
        var bench = new GhostSwapBench().Start();

        var refused = bench.Refused(bench.Swap(new Selector.Box(At(0, 0, 0), At(1, 1, 1)), Lattice.Tests.Selectors.UnknownCase.Of<SwapTo>()));

        Assert.Equal(ReasonCodes.UnknownKind, refused.Why.Code);
    }

    [Fact]
    public void Revert_is_refused_after_another_actor_changed_a_swapped_ghost()
    {
        var bench = new GhostSwapBench().Start();
        var (run, plan) = PlannedRun(bench);
        var swapped = bench.Applied(bench.Swap(new Selector.InPlan(plan), bench.Heavy));
        bench.Applied(new Command.Change(TestBoard.Ids(run[2]), new GhostEdit.SetLabel(Option<Label>.None), Option<Revision>.None), TestBoard.Bob);

        var refused = bench.Refused(new Command.Revert(swapped.Id));

        Assert.Equal(ReasonCodes.ChangedSince, refused.Why.Code);
        Assert.Contains(run[2], refused.Ghosts);
    }

    [Fact]
    public void A_ghost_changed_after_the_revision_seen_refuses_changed_since()
    {
        var bench = new GhostSwapBench().Start();
        var cable = Place(bench, Straight, At(0, 0, 0));
        var seen = bench.View.Revision;
        bench.Applied(new Command.Change(TestBoard.Ids(cable), new GhostEdit.SetLabel(Option.Some(Name)), Option<Revision>.None), TestBoard.Bob);

        var refused = bench.Refused(bench.Swap(TestBoard.Ids(cable), bench.Heavy, Option.Some(seen)));

        Assert.Equal(ReasonCodes.ChangedSince, refused.Why.Code);
        Assert.Equal(cable, Assert.Single(refused.Ghosts));
    }

    [Fact]
    public void A_mixed_drag_swaps_built_and_planned_pieces_in_one_change_set_and_one_revert()
    {
        var bench = new GhostSwapBench();
        var plain = bench.Runs.Real(Straight, At(0, 0, 0), bench.AlongX);
        var withSwap = bench.Runs.Real(Straight, At(2, 0, 0), bench.AlongX);
        bench.Start();
        var planned = Place(bench, Straight, At(1, 0, 0), bench.AlongX);
        var swap = Place(bench, new Intent.Swap(withSwap, bench.At("ModArmouredCable", At(2, 0, 0), bench.AlongX), Option<Finish>.None));
        var (corner, opposite) = (At(-1, -1, -1), At(3, 1, 1));

        var changes = bench.Applied(new Command.Batch(ValueList.From(new Command[]
        {
            new Command.PlanSwap(SwapBench.Refs(plain, withSwap), bench.Heavy, Option<PlanId>.None, SwapBench.Asker),
            bench.Swap(new Selector.Box(corner, opposite), bench.Heavy, Option.Some(bench.View.Revision)),
        })));

        var added = Assert.Single(changes.Added);
        Assert.Equal(plain, Assert.IsType<Intent.Swap>(added.Intent).Old);
        Assert.Equal(new[] { planned, swap }.OrderBy(static id => id.Value), ChangedIds(changes).OrderBy(static id => id.Value));
        Assert.All(new[] { added.Id, planned, swap }, id => Assert.Equal(HeavyStraight, bench.Name(GhostSwapBench.PlacementOf(bench.Now(id)).Piece)));
        Assert.Equal(withSwap, Assert.IsType<Intent.Swap>(bench.Now(swap).Intent).Old);

        bench.Applied(new Command.Revert(changes.Id));

        Assert.Empty(bench.View.On(plain));
        Assert.Equal(Straight, bench.Name(GhostSwapBench.PlacementOf(bench.Now(planned)).Piece));
        Assert.Equal("ModArmouredCable", bench.Name(GhostSwapBench.PlacementOf(bench.Now(swap)).Piece));
    }

    [Fact]
    public void Plan_swap_on_a_structure_with_a_swap_ghost_keeps_that_ghost_instead_of_replacing_it()
    {
        var bench = new GhostSwapBench();
        var built = bench.Runs.Real(Straight, At(0, 0, 0), bench.AlongX);
        bench.Start();
        var plan = NewPlan(bench, "Upgrades");
        var swap = Assert.Single(bench.Boards.Placed(Carrying(
            new Intent.Swap(built, bench.At("ModArmouredCable", At(0, 0, 0), bench.AlongX), Option<Finish>.None), plan)).Added);
        var into = NewPlan(bench, "Elsewhere");

        var changes = bench.Applied(new Command.PlanSwap(
            new StructureSelector.Refs(ValueList.Of(built)), bench.Heavy, Option.Some(into), SwapBench.Asker));

        Assert.Empty(changes.Added);
        Assert.Empty(changes.Removed);
        var now = bench.Now(swap.Id);
        Assert.Equal(HeavyStraight, bench.Name(GhostSwapBench.PlacementOf(now).Piece));
        Assert.Equal(plan, now.Plan);
        Assert.Equal(swap.Label, now.Label);
        Assert.Equal(swap.MadeBy, now.MadeBy);
        Assert.Equal(swap.Attached, now.Attached);
    }

    [Fact]
    public void A_policy_that_refuses_edits_in_another_players_plan_refuses_the_swap_for_that_ghost()
    {
        var bench = new GhostSwapBench().Start();
        var mine = Place(bench, Straight, At(0, 0, 0));
        var bobsPlan = TestBoard.Applied(bench.Boards.Apply(new Command.CreatePlan(new PlanDraft(
            TestKitIds.Must(PlanName.Create("Bob's")), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty)), TestBoard.Bob)).PlansOpened[0].Id;
        var his = Assert.Single(bench.Boards.Placed(TestBoard.Draft(Build(bench, Straight, At(3, 0, 0)), Option.Some(bobsPlan))).Added).Id;
        var notYours = TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "not-yours"));
        bench.Boards.Policies.Install(new AccessPolicy((_, _) => true, (_, _) => true, ask =>
            ask.Command is Command.Change && ask.PlanRecord.TryGet(out var record) && record.Owner == TestBoard.Bob && ask.Actor != TestBoard.Bob
                ? Result<Unit, Reason>.Error(Reason.Of(notYours, SwapCodes.Swaps, ValueMap<string, string>.Empty))
                : Result<Unit, Reason>.Ok(Unit.Value)));

        var refused = bench.Refused(bench.Swap(TestBoard.Ids(mine, his), bench.Heavy));

        Assert.Equal(notYours, refused.Why.Code);
        Assert.Equal(his, Assert.Single(refused.Ghosts));
        Assert.Equal(Straight, bench.Name(GhostSwapBench.PlacementOf(bench.Now(mine)).Piece));
    }

    private static Finding Verdict(GhostSwapBench bench, GhostId ghost)
    {
        var answer = TestKitIds.Must(bench.Preview.Validation.Check(TestBoard.Ids(ghost)));
        var report = Assert.IsType<Answer<ValidationReport>.Ready>(answer).Value;
        return report.Findings.First(f => f.Ghost == ghost && f is not Finding.Warns);
    }

    private static Bill BillOf(GhostSwapBench bench, GhostId ghost) =>
        BillMaker.Of(new[] { TestKitIds.Must(bench.Preview.Steps.For(ghost)) });
}
