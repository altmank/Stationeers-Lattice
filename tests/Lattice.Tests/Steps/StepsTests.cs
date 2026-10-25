using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;
using Lattice.Core.Steps;
using Lattice.Core.Support;
using Lattice.Tests.Board;
using Lattice.Tests.Catalogue;
using Lattice.Tests.Matching;
using Lattice.Tests.Selectors;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Steps.StepsBench;

namespace Lattice.Tests.Steps;

public sealed class StepsTests
{
    // The Steel Frame as the game's prefab builds it (tests/fixtures/generated/catalogue.lcat, StructureFrame): the kit
    // places state 0; state 1 and 2 weld one steel sheet each; state 3 screws on one plastic sheet. Each state's exit
    // tool takes it back off (Structure.AttackWith, Assets.Scripts.Objects/Structure.cs). Option 9 keeps it apart from the
    // hand catalogue's own frame in the same kit.
    private const string SteelFrame =
        "piece TestSteelFrame kit=ItemSteelFrames option=9 grid=large\n" +
        "  step material=ItemSteelFrames*1 exit=ItemAngleGrinder air=no\n" +
        "  step entry=ItemWeldingTorch material=ItemSteelSheets*1 exit=ItemWrench air=no gravity=yes\n" +
        "  step entry=ItemWeldingTorch material=ItemSteelSheets*1 exit=ItemWrench air=yes gravity=yes\n" +
        "  step entry=ItemScrewdriver material=ItemPlasticSheets*1 exit=ItemCrowbar air=yes gravity=yes\n" +
        "  cell 0 0 0 centre\n";

    // A mod's cable kit: two pieces the kit merges into each other (MultiMergeConstructor.Construct), a junction costing
    // one coil more than a straight.
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
        "piece ModCableCorner kit=ItemModCoil option=2 grid=small\n" +
        "  step material=ItemModCoil*1 exit=ItemModSnips air=no\n" +
        "  end 0 0 0 x- mod:ModGrid:1024 reach=340\n" +
        "  end 0 0 0 z+ mod:ModGrid:1024 reach=400\n" +
        "  cell 0 0 0 cable\n" +
        "  collision 2\n";

    // A modded frame: a kit no base game has, a modded tool and a modded material.
    private const string ModFrame =
        "piece ModFrame kit=ItemModFrameKit option=0 grid=large\n" +
        "  step material=ItemModFrameKit*1 exit=ItemModSpanner air=no\n" +
        "  step entry=ItemModRivetGun material=ItemModPlate*3 exit=ItemModSpanner air=yes gravity=yes\n" +
        "  cell 0 0 0 centre\n" +
        "piece ModSpawnedThing grid=large\n" +
        "  step exit=ItemModSpanner air=no\n" +
        "  cell 0 0 0 centre\n";

    private static Placement Put(StepsBench bench, string piece, Cell at, Turn turn = default) => bench.W.Placement(piece, at, turn);

    [Fact]
    public void A_steel_frame_on_empty_ground_is_its_kit_two_steel_sheets_and_a_plastic_sheet_with_tools_in_game_order()
    {
        var bench = new StepsBench(Pieces(SteelFrame));
        var target = Put(bench, "TestSteelFrame", TestWorld.Large);
        var ghost = bench.W.Place(new Intent.Build(target, Option<Finish>.None));
        bench.Run();

        var plan = bench.Steps(ghost.Id);

        var frame = PlacedBy(0);
        Step[] expected =
        [
            new Step.PlaceKit(bench.Kit("ItemSteelFrames"), OptionOf(9), Of("ItemSteelFrames", 1), target),
            new Step.Advance(frame, Item("ItemWeldingTorch"), Some("ItemSteelSheets", 1)),
            new Step.Advance(frame, Item("ItemWeldingTorch"), Some("ItemSteelSheets", 1)),
            new Step.Advance(frame, Item("ItemScrewdriver"), Some("ItemPlasticSheets", 1)),
        ];
        Assert.Equal(new StepPlan(ghost.Id, ValueList.From(expected), ValueList<Constraint>.Empty), plan);
    }

    [Fact]
    public void The_steel_frame_bill_is_one_kit_two_steel_sheets_one_plastic_sheet_a_welder_and_a_screwdriver()
    {
        var bench = new StepsBench(Pieces(SteelFrame));
        var ghost = bench.W.Build("TestSteelFrame", TestWorld.Large);
        bench.Run();

        var bill = bench.Bill(TestBoard.Ids(ghost.Id));

        Assert.Equal(ValueList.From([Of("ItemSteelFrames", 1), Of("ItemSteelSheets", 2), Of("ItemPlasticSheets", 1)]), bill.Needed);
        Assert.Empty(bill.GivenBack);
        Assert.Equal(ValueSet.From([Item("ItemWeldingTorch"), Item("ItemScrewdriver")]), bill.Tools);
    }

    [GameCatalogueFact]
    public void The_game_catalogue_steel_frame_bills_its_kit_two_steel_sheets_and_a_plastic_sheet()
    {
        var game = GameCatalogue.Load().Pieces.First(piece => piece.Name == "StructureFrame");
        var renamed = game with
        {
            Name = "GameSteelFrame",
            Facts = game.Facts with
            {
                Key = PrefabHash.Piece("GameSteelFrame"),
                Kits = ValueList.From([new KitPlace(game.Facts.Kits[0].Kit, OptionOf(9))]),
            },
        };
        var bench = new StepsBench(renamed);
        var ghost = bench.W.Build("GameSteelFrame", TestWorld.Large);
        bench.Run();

        var bill = bench.Bill(TestBoard.Ids(ghost.Id));

        Assert.Equal(ValueList.From([Of("ItemSteelFrames", 1), Of("ItemSteelSheets", 2), Of("ItemPlasticSheets", 1)]), bill.Needed);
        Assert.Equal(ValueSet.From([Item("ItemWeldingTorch"), Item("ItemScrewdriver")]), bill.Tools);
    }

    [Fact]
    public void A_swap_of_cable_to_heavy_cable_takes_the_cable_back_and_places_the_heavy_kit()
    {
        var bench = new StepsBench();
        var old = bench.W.Structure("StructureCableStraight", TestWorld.Small);
        bench.W.World.Load(old);
        var target = Put(bench, "StructureCableStraightH", TestWorld.Small);
        var ghost = bench.W.Place(new Intent.Swap(old.Ref, target, Option<Finish>.None));
        bench.Run();

        var plan = bench.Steps(ghost.Id);

        Step[] expected =
        [
            new Step.TakeBack(old.Ref, Item("ItemWireCutters"), ValueList.From([Of("ItemCableCoil", 1)])),
            new Step.PlaceKit(bench.Kit("ItemCableCoilHeavy"), OptionOf(0), Of("ItemCableCoilHeavy", 1), target),
        ];
        Assert.Equal(ValueList.From(expected), plan.Steps);
    }

    [Fact]
    public void The_heavy_cable_swap_bill_needs_a_heavy_coil_gives_a_coil_back_and_nets_both()
    {
        var bench = new StepsBench();
        var old = bench.W.Structure("StructureCableStraight", TestWorld.Small);
        bench.W.World.Load(old);
        var ghost = bench.W.Place(new Intent.Swap(old.Ref, Put(bench, "StructureCableStraightH", TestWorld.Small), Option<Finish>.None));
        bench.Run();

        var bill = bench.Bill(TestBoard.Ids(ghost.Id));

        Assert.Equal(ValueList.From([Of("ItemCableCoilHeavy", 1)]), bill.Needed);
        Assert.Equal(ValueList.From([Of("ItemCableCoil", 1)]), bill.GivenBack);
        Assert.Equal(
            ValueList.From([new ItemDelta(Item("ItemCableCoilHeavy"), 1), new ItemDelta(Item("ItemCableCoil"), -1)]),
            bill.Net);
        Assert.Equal(ValueSet.Of(Item("ItemWireCutters")), bill.Tools);
    }

    [Fact]
    public void A_bill_over_a_plan_equals_the_sum_of_its_ghosts_steps()
    {
        var bench = new StepsBench(Pieces(SteelFrame));
        var old = bench.W.Structure("StructureCableStraight", TestWorld.Small);
        var gone = bench.W.Structure("StructurePipeStraight", new Cell(105, 100, 100));
        bench.W.World.Load(old);
        bench.W.World.Load(gone);
        var ghosts = new[]
        {
            bench.W.Build("TestSteelFrame", TestWorld.Large),
            bench.W.Build("TestSteelFrame", new Cell(30, 10, 10)),
            bench.W.Place(new Intent.Swap(old.Ref, Put(bench, "StructureCableStraightH", TestWorld.Small), Option<Finish>.None)),
            bench.W.Place(new Intent.Remove(gone.Ref)),
            bench.W.Build("StructureWallIron", new Cell(50, 10, 10)),
        };
        bench.Run();

        var bill = bench.Bill(new Selector.InPlan(GhostBoard.DefaultPlan));

        var needed = new Dictionary<ItemKey, int>();
        var givenBack = new Dictionary<ItemKey, int>();
        foreach (var ghost in ghosts)
            foreach (var step in bench.Steps(ghost.Id).Steps)
                switch (step)
                {
                    case Step.PlaceKit place: Add(needed, place.Uses); break;
                    case Step.Advance { Material: var material } when material.TryGet(out var used): Add(needed, used); break;
                    case Step.TakeBack back: foreach (var item in back.Returns) Add(givenBack, item); break;
                }

        Assert.Equal(needed, bill.Needed.ToDictionary(q => q.Item, q => q.Count));
        Assert.Equal(givenBack, bill.GivenBack.ToDictionary(q => q.Item, q => q.Count));
        foreach (var delta in bill.Net)
            Assert.Equal(needed.GetValueOrDefault(delta.Item) - givenBack.GetValueOrDefault(delta.Item), delta.Change);
        Assert.Equal(needed.Keys.Union(givenBack.Keys).Count(k => needed.GetValueOrDefault(k) != givenBack.GetValueOrDefault(k)), bill.Net.Count);

        static void Add(Dictionary<ItemKey, int> into, ItemQuantity quantity) =>
            into[quantity.Item] = into.GetValueOrDefault(quantity.Item) + quantity.Count;
    }

    [Fact]
    public void A_modded_piece_is_built_with_its_own_kit_tool_and_material()
    {
        var bench = new StepsBench(Pieces(ModFrame));
        var target = Put(bench, "ModFrame", TestWorld.Large);
        var ghost = bench.W.Place(new Intent.Build(target, Option<Finish>.None));
        bench.Run();

        var plan = bench.Steps(ghost.Id);

        Step[] expected =
        [
            new Step.PlaceKit(bench.Kit("ItemModFrameKit"), OptionOf(0), Of("ItemModFrameKit", 1), target),
            new Step.Advance(PlacedBy(0), Item("ItemModRivetGun"), Some("ItemModPlate", 3)),
        ];
        Assert.Equal(ValueList.From(expected), plan.Steps);
    }

    [Fact]
    public void A_started_ghost_continues_from_the_step_its_structure_is_at()
    {
        var bench = new StepsBench(Pieces(SteelFrame));
        var ghost = bench.W.Build("TestSteelFrame", TestWorld.Large);
        var partBuilt = bench.W.Structure("TestSteelFrame", TestWorld.Large, state: TestWorld.Step(1));
        bench.W.World.Load(partBuilt);
        bench.Run();
        Assert.IsType<GhostState.Started>(bench.W.StateOf(ghost.Id));

        var plan = bench.Steps(ghost.Id);

        Step[] expected =
        [
            new Step.Advance(Standing(partBuilt.Ref), Item("ItemWeldingTorch"), Some("ItemSteelSheets", 1)),
            new Step.Advance(Standing(partBuilt.Ref), Item("ItemScrewdriver"), Some("ItemPlasticSheets", 1)),
        ];
        Assert.Equal(ValueList.From(expected), plan.Steps);
    }

    [Fact]
    public void A_build_step_with_no_tool_but_its_material_is_done_with_the_material_in_hand_and_bills_no_tool()
    {
        var bench = new StepsBench();
        var target = Put(bench, "StructureWallIron", TestWorld.Large);
        var ghost = bench.W.Place(new Intent.Build(target, Option<Finish>.None));
        bench.Run();

        var advance = Assert.IsType<Step.Advance>(bench.Steps(ghost.Id).Steps[1]);
        var bill = bench.Bill(TestBoard.Ids(ghost.Id));

        Assert.Equal(new Step.Advance(PlacedBy(0), Item("ItemIronSheets"), Some("ItemIronSheets", 2)), advance);
        Assert.DoesNotContain(Item("ItemIronSheets"), bill.Tools);
    }

    [Fact]
    public void A_removal_takes_each_built_state_back_last_first_and_each_gives_back_its_material()
    {
        var bench = new StepsBench(Pieces(SteelFrame));
        var frame = bench.W.Structure("TestSteelFrame", TestWorld.Large, state: TestWorld.Step(2));
        bench.W.World.Load(frame);
        var ghost = bench.W.Place(new Intent.Remove(frame.Ref));
        bench.Run();

        var plan = bench.Steps(ghost.Id);

        Step[] expected =
        [
            new Step.TakeBack(frame.Ref, Item("ItemWrench"), ValueList.From([Of("ItemSteelSheets", 1)])),
            new Step.TakeBack(frame.Ref, Item("ItemWrench"), ValueList.From([Of("ItemSteelSheets", 1)])),
            new Step.TakeBack(frame.Ref, Item("ItemAngleGrinder"), ValueList.From([Of("ItemSteelFrames", 1)])),
        ];
        Assert.Equal(ValueList.From(expected), plan.Steps);
    }

    [Fact]
    public void A_swap_within_a_merging_kit_is_one_merge_paying_the_kit_cost_difference()
    {
        var bench = new StepsBench(Merging("ItemModSnips", Pieces(ModCable)));
        var old = bench.W.Structure("ModCableStraight", TestWorld.Small);
        bench.W.World.Load(old);
        var junction = Put(bench, "ModCableJunction", TestWorld.Small);
        var ghost = bench.W.Place(new Intent.Swap(old.Ref, junction, Option<Finish>.None));
        bench.Run();

        var plan = bench.Steps(ghost.Id);
        var bill = bench.Bill(TestBoard.Ids(ghost.Id));

        Assert.Equal(
            ValueList.From<Step>([new Step.Merge(old.Ref, junction.Piece, Item("ItemModSnips"), Some("ItemModCoil", 1))]),
            plan.Steps);
        Assert.Equal(ValueList.From([Of("ItemModCoil", 1)]), bill.Needed);
        Assert.Equal(ValueSet.Of(Item("ItemModSnips")), bill.Tools);
    }

    [Fact]
    public void A_merge_that_would_drop_one_of_the_old_ends_is_a_take_apart_and_a_new_kit()
    {
        var bench = new StepsBench(Merging("ItemModSnips", Pieces(ModCable)));
        var old = bench.W.Structure("ModCableStraight", TestWorld.Small);
        bench.W.World.Load(old);
        var corner = Put(bench, "ModCableCorner", TestWorld.Small);
        var ghost = bench.W.Place(new Intent.Swap(old.Ref, corner, Option<Finish>.None));
        bench.Run();

        var plan = bench.Steps(ghost.Id);

        Assert.IsType<Step.TakeBack>(plan.Steps[0]);
        Assert.IsType<Step.PlaceKit>(plan.Steps[1]);
    }

    [Fact]
    public void Steps_after_a_merge_name_the_merge_step_as_the_structure_they_act_on()
    {
        var bench = new StepsBench(Merging("ItemModSnips", Pieces(ModCable)));
        var old = bench.W.Structure("ModCableStraight", TestWorld.Small);
        bench.W.World.Load(old);
        var colour = TestKitIds.Must(PaintColour.Create(3));
        var paint = TestKitIds.Must(Finish.Create(Option.Some(colour), Option<ThingName>.None, Option<Setting>.None));
        var ghost = bench.W.Place(new Intent.Swap(old.Ref, Put(bench, "ModCableJunction", TestWorld.Small), Option.Some(paint)));
        bench.Run();

        var steps = bench.Steps(ghost.Id).Steps;

        Assert.IsType<Step.Merge>(steps[0]);
        Assert.Equal(new Step.Paint(PlacedBy(0), colour), steps[1]);
    }

    [Fact]
    public void Steps_after_a_take_apart_and_a_new_kit_name_the_kit_step_by_its_index()
    {
        var bench = new StepsBench(Merging("ItemModSnips", Pieces(ModCable)));
        var old = bench.W.Structure("ModCableStraight", TestWorld.Small);
        bench.W.World.Load(old);
        var colour = TestKitIds.Must(PaintColour.Create(3));
        var paint = TestKitIds.Must(Finish.Create(Option.Some(colour), Option<ThingName>.None, Option<Setting>.None));
        var ghost = bench.W.Place(new Intent.Swap(old.Ref, Put(bench, "ModCableCorner", TestWorld.Small), Option.Some(paint)));
        bench.Run();

        var steps = bench.Steps(ghost.Id).Steps;

        var kit = System.Linq.Enumerable.ToList(steps).FindIndex(static step => step is Step.PlaceKit);
        Assert.True(kit > 0);
        Assert.Equal(new Step.Paint(PlacedBy(kit), colour), steps[steps.Count - 1]);
    }

    [Fact]
    public void A_build_with_a_finish_paints_names_and_sets_after_the_last_build_step()
    {
        var bench = new StepsBench();
        var colour = TestKitIds.Must(PaintColour.Create(3));
        var name = TestKitIds.Must(ThingName.Create("Main bus"));
        var setting = TestKitIds.Must(Setting.Create(2));
        var finish = TestKitIds.Must(Finish.Create(Option.Some(colour), Option.Some(name), Option.Some(setting)));
        var target = Put(bench, "StructureCableStraight", TestWorld.Small);
        var ghost = bench.W.Place(new Intent.Build(target, Option.Some(finish)));
        bench.Run();

        var steps = bench.Steps(ghost.Id).Steps;

        var cable = PlacedBy(0);
        Assert.Equal(
            ValueList.From<Step>([
                new Step.PlaceKit(bench.Kit("ItemCableCoil"), OptionOf(0), Of("ItemCableCoil", 1), target),
                new Step.Paint(cable, colour), new Step.Rename(cable, name), new Step.SetValue(cable, setting)]),
            steps);
    }

    [Fact]
    public void A_finish_on_a_structure_asks_only_what_it_does_not_have_yet()
    {
        var bench = new StepsBench();
        var colour = TestKitIds.Must(PaintColour.Create(3));
        var name = TestKitIds.Must(ThingName.Create("Main bus"));
        var built = bench.W.Structure("StructureCableStraight", TestWorld.Small) with { Colour = Option.Some(colour) };
        bench.W.World.Load(built);
        var finish = TestKitIds.Must(Finish.Create(Option.Some(colour), Option.Some(name), Option<Setting>.None));
        var ghost = bench.W.Place(new Intent.FinishOnly(built.Ref, finish));
        bench.Run();

        Assert.Equal(ValueList.From<Step>([new Step.Rename(Standing(built.Ref), name)]), bench.Steps(ghost.Id).Steps);
    }

    [Fact]
    public void Steps_carry_one_pipe_join_per_tick_for_a_joining_ghost_and_after_for_each_ghost_it_waits_for()
    {
        var bench = new StepsBench();
        var first = bench.W.Build("StructurePipeStraight", TestWorld.Small);
        var joining = bench.W.Build("StructurePipeStraight", new Cell(105, 100, 100));
        bench.Order.Joins(joining.Id);
        bench.Order.Waits(joining.Id, first.Id);
        bench.Run();

        Assert.Equal(
            ValueList.From<Constraint>([new Constraint.After(first.Id), new Constraint.OnePipeJoinPerTick()]),
            bench.Steps(joining.Id).Constraints);
        Assert.Empty(bench.Steps(first.Id).Constraints);
    }

    [Fact]
    public void The_order_report_gives_the_waits_and_the_pipe_joins()
    {
        var a = new GhostId(1);
        var b = new GhostId(2);
        var report = new OrderReport(
            ValueList.From([ValueList.From([a]), ValueList.From([b])]),
            ValueList.From<Finding>([new Finding.Passes(a), new Finding.WaitsFor(b, ValueList.From([a]))]),
            ValueSet.Of(b));

        var order = new ReportOrder(report);

        Assert.Equal(ValueList.From([a]), order.WaitsFor(b));
        Assert.Empty(order.WaitsFor(a));
        Assert.True(order.JoinsPipes(b));
        Assert.False(order.JoinsPipes(a));
    }

    [Fact]
    public void An_unknown_ghost_is_refused_not_found()
    {
        var bench = new StepsBench();
        bench.Run();

        Assert.Equal(ReasonCodes.NotFound, bench.Refusal(new GhostId(999)).Code);
    }

    [Fact]
    public void A_stale_ghost_is_refused_with_the_reason_it_is_stale()
    {
        var bench = new StepsBench();
        var ghost = bench.W.Build("StructureCableStraight", TestWorld.Small);
        bench.W.World.Load(bench.W.Structure("StructureCableCorner", TestWorld.Small));
        bench.Run();
        var stale = Assert.IsType<GhostState.Stale>(bench.W.StateOf(ghost.Id));

        Assert.Equal(stale.Why, bench.Refusal(ghost.Id));
    }

    [Fact]
    public void A_piece_no_kit_places_cannot_be_built_and_says_so()
    {
        var bench = new StepsBench(Pieces(ModFrame));
        var ghost = bench.W.Build("ModSpawnedThing", TestWorld.Large);
        bench.Run();

        var why = bench.Refusal(ghost.Id);

        Assert.Equal(ReasonCodes.UnknownPiece, why.Code);
        Assert.Equal("no-kit", why.Facts["problem"]);
    }

    [Fact]
    public void Removing_a_broken_structure_is_one_take_apart_with_a_broken_tool_that_gives_nothing_back()
    {
        var bench = new StepsBench();
        var broken = bench.W.Structure("StructureCableStraight", TestWorld.Small, state: new BuildState.Broken());
        bench.W.World.Load(broken);
        var ghost = bench.W.Place(new Intent.Remove(broken.Ref));
        bench.Run();

        var steps = bench.Steps(ghost.Id).Steps;

        var takeBack = Assert.IsType<Step.TakeBack>(Assert.Single(steps));
        Assert.Equal(broken.Ref, takeBack.On);
        Assert.Contains(takeBack.Tool, bench.Catalogue.BrokenTools);
        Assert.Empty(takeBack.Returns);
    }

    [Fact]
    public void Removing_a_broken_structure_is_refused_no_tool_when_the_catalogue_names_no_broken_tool()
    {
        var bench = StepsBench.WithoutBrokenTools();
        var broken = bench.W.Structure("StructureCableStraight", TestWorld.Small, state: new BuildState.Broken());
        bench.W.World.Load(broken);
        var ghost = bench.W.Place(new Intent.Remove(broken.Ref));
        bench.Run();

        var why = bench.Refusal(ghost.Id);

        Assert.Equal(ReasonCodes.NoTool, why.Code);
        Assert.Equal("broken", why.Facts["way"]);
        Assert.False(why.Facts.ContainsKey("step"));
    }

    [Fact]
    public void A_bill_leaves_out_ghosts_that_cannot_be_built()
    {
        var bench = new StepsBench(Pieces(ModFrame));
        var buildable = bench.W.Build("ModFrame", TestWorld.Large);
        var kitless = bench.W.Build("ModSpawnedThing", new Cell(30, 10, 10));
        bench.Run();

        Assert.Equal(bench.Bill(TestBoard.Ids(buildable.Id)), bench.Bill(TestBoard.Ids(buildable.Id, kitless.Id)));
    }

    [Fact]
    public void A_bill_is_kept_until_the_board_or_the_world_changes()
    {
        var bench = new StepsBench(Pieces(SteelFrame));
        bench.W.Build("TestSteelFrame", TestWorld.Large);
        bench.Run();
        var which = new Selector.InPlan(GhostBoard.DefaultPlan);

        var first = bench.Bill(which);
        Assert.Same(first, bench.Bill(which));

        bench.W.Build("TestSteelFrame", new Cell(30, 10, 10));
        var afterBoard = bench.Bill(which);
        Assert.NotSame(first, afterBoard);

        bench.W.World.Register(bench.W.Structure("TestSteelFrame", new Cell(50, 10, 10), state: TestWorld.Step(0)));
        Assert.NotSame(afterBoard, bench.Bill(which));
    }

    [Fact]
    public void An_unknown_step_kind_or_intent_kind_is_refused_unknown_kind()
    {
        var unknown = UnknownCase.Of<Intent>();

        var why = StepPlanner.Refuse(unknown);

        Assert.Equal(ReasonCodes.UnknownKind, why.Code);
    }
}
