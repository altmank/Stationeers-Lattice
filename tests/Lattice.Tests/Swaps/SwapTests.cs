using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Commands;
using Lattice.Core.Slices;
using Lattice.Core.Swaps;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Selectors;
using Xunit;
using static Lattice.Tests.Runs.RunBench;
using static Lattice.Tests.Swaps.SwapBench;

namespace Lattice.Tests.Swaps;

public sealed class SwapTests
{
    private const string Straight = "StructureCableStraight";
    private const string Corner = "StructureCableCorner";
    private const string HeavyStraight = "StructureCableStraightH";
    private const string Wall = "StructureWallIron";
    private const string Window = "StructureCompositeWindowIron";

    private static NetworkRef NetworkOf(SwapBench bench, StructureRef structure) =>
        bench.Runs.World.NetworkOf(structure.Id).TryGet(out var network) ? network : throw new InvalidOperationException("no network");

    private static Placement PlacementOf(SwapBench bench, StructureRef structure) =>
        new(structure.Piece, structure.At, bench.Info(structure).Turn);

    [Fact]
    public void Every_cable_on_a_network_swaps_to_heavy_in_one_command()
    {
        var bench = new SwapBench();
        var alongX = bench.Runs.Sending(Direction.ZPlus, Direction.XPlus);
        var cables = new List<StructureRef>
        {
            bench.Real(Straight, At(0, 0, 0), alongX),
            bench.Real(Straight, At(1, 0, 0), alongX),
            bench.Real(Straight, At(2, 0, 0), alongX),
            bench.Real(Corner, At(3, 0, 0)),
            bench.Real(Straight, At(3, 0, 1)),
            bench.Real(Straight, At(3, 0, 2)),
        };
        var apart = bench.Real(Straight, At(0, 0, 6), alongX);
        var network = NetworkOf(bench, cables[0]);
        Assert.All(cables, cable => Assert.Equal(network, NetworkOf(bench, cable)));
        Assert.NotEqual(network, NetworkOf(bench, apart));

        var drafts = bench.Drafts(new StructureSelector.Network(network), new SwapTo.SameShapeFrom(bench.Kit(HeavyStraight)));

        var swaps = drafts.Select(draft => Assert.IsType<Intent.Swap>(draft.Intent)).ToList();
        Assert.Equal(cables.Select(c => c.Id.Value).OrderBy(id => id), swaps.Select(s => s.Old.Id.Value).OrderBy(id => id));
        foreach (var swap in swaps)
        {
            Assert.Equal(bench.Name(swap.Old.Piece) + "H", bench.Name(swap.New.Piece));
            Assert.Equal(swap.Old.At, swap.New.At);
            Assert.Equal(bench.Runs.WaysOf(PlacementOf(bench, swap.Old), swap.Old.At), bench.Runs.WaysOf(swap.New, swap.New.At));
        }

        TestBoard.Applied(bench.Runs.Boards.Apply(new Command.Place(drafts)));
        Assert.All(cables, cable => Assert.Single(bench.Runs.Boards.View.On(cable)));
        Assert.Empty(bench.Runs.Boards.View.On(apart));
    }

    [Fact]
    public void A_window_swap_on_a_face_over_its_rating_gives_a_finding_with_the_numbers()
    {
        var bench = new SwapBench();
        var wall = bench.Real(Wall, Large(0, 0, 0));
        bench.SetDifference(Large(0, 0, 0), 45);

        var drafts = bench.Drafts(Refs(wall), new SwapTo.Exactly(bench.Piece(Window)));
        var ghost = Assert.Single(TestBoard.Applied(bench.Runs.Boards.Apply(new Command.Place(drafts))).Added);

        var warning = Assert.IsType<RuleVerdict.Warn>(new SwapFaceRule(bench.Planner).Check(new RuleInput(ghost, bench.Runs.Query)));
        var (cell, _) = bench.FaceZMinusOf(Large(0, 0, 0));
        Assert.Equal(ReasonCodes.FaceOverRating, warning.Why.Code);
        Assert.Equal(SwapCodes.Swaps, warning.Why.From);
        Assert.Equal("40", warning.Why.Facts["rating"]);
        Assert.Equal("45", warning.Why.Facts["difference"]);
        Assert.Equal($"{cell.X},{cell.Y},{cell.Z}", warning.Why.Facts["cell"]);
        Assert.Equal("z+", warning.Why.Facts["face"]);
    }

    [Fact]
    public void Options_show_the_new_piece_rating_against_the_face_difference_now()
    {
        var bench = new SwapBench();
        var wall = bench.Real(Wall, Large(0, 0, 0));
        bench.SetDifference(Large(0, 0, 0), -45);

        var options = bench.Options(wall).ToDictionary(o => bench.Name(o.New.Piece), o => o.Face);

        var (cell, face) = bench.FaceZMinusOf(Large(0, 0, 0));
        Assert.Equal(Option.Some(new FaceLoad(cell, face, new Kilopascals(40), new Kilopascals(45))), options[Window]);
        Assert.True(options[Window].TryGet(out var window) && window.Over);
        Assert.True(options["ModPanel"].TryGet(out var panel) && !panel.Over);
        Assert.True(options["ModPane"].TryGet(out var pane) && pane.Over);
    }

    [Fact]
    public void A_swap_under_its_rating_or_onto_no_face_passes()
    {
        var bench = new SwapBench();
        var wall = bench.Real(Wall, Large(0, 0, 0));
        var cable = bench.Real(Straight, Origin);
        bench.SetDifference(Large(0, 0, 0), 39);
        var drafts = bench.Drafts(Refs(wall, cable), new SwapTo.Exactly(bench.Piece(Window)))
            .Concat(bench.Drafts(Refs(cable), new SwapTo.SameShapeFrom(bench.Kit(HeavyStraight))));
        var rule = new SwapFaceRule(bench.Planner);

        var ghosts = TestBoard.Applied(bench.Runs.Boards.Apply(new Command.Place(ValueList.From(drafts)))).Added;

        Assert.Equal(2, ghosts.Count);
        Assert.All(ghosts, ghost => Assert.IsType<RuleVerdict.Pass>(rule.Check(new RuleInput(ghost, bench.Runs.Query))));
    }

    [Fact]
    public void A_wall_can_become_a_window_or_a_modded_panel_and_back_never_a_frame()
    {
        var bench = new SwapBench();
        var wall = bench.Real(Wall, Large(0, 0, 0));
        var pane = bench.Real("ModPane", Large(1, 0, 0));

        var fromWall = bench.Options(wall).Select(o => bench.Name(o.New.Piece)).ToList();
        var fromPane = bench.Options(pane).Select(o => bench.Name(o.New.Piece)).ToList();

        Assert.Equal(new[] { "ModPane", "ModPanel", Window }, fromWall.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(new[] { "ModPanel", Window, Wall }, fromPane.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void A_cable_can_become_another_piece_with_its_ends_and_kinds_never_a_pipe_or_another_shape()
    {
        var bench = new SwapBench();
        var cable = bench.Real(Straight, Origin);

        var names = bench.Options(cable).Select(o => bench.Name(o.New.Piece)).ToList();

        Assert.Contains(HeavyStraight, names);
        Assert.Contains("ModConduitLine", names);
        Assert.DoesNotContain("StructurePipeStraight", names);
        Assert.DoesNotContain(Corner, names);
        Assert.DoesNotContain(Straight, names);
    }

    [Fact]
    public void The_old_turn_is_kept_when_it_fits()
    {
        var bench = new SwapBench();
        var turn = bench.Runs.Sending(Direction.ZPlus, Direction.YPlus);
        var cable = bench.Real(Straight, Origin, turn);

        var heavy = Assert.Single(bench.Options(cable), o => bench.Name(o.New.Piece) == HeavyStraight);

        Assert.Equal(new Placement(bench.Piece(HeavyStraight), Origin, turn), heavy.New);
        Assert.True(heavy.Face.IsNone);
    }

    [Fact]
    public void A_modded_conduit_swaps_to_heavy_cable_under_a_turn()
    {
        var bench = new SwapBench();
        var conduit = bench.Real("ModConduitLine", Origin);

        var placement = TestKitIds.Must(bench.Planner.Target(bench.Info(conduit), new SwapTo.SameShapeFrom(bench.Kit(HeavyStraight))));

        Assert.Equal(bench.Piece(HeavyStraight), placement.Piece);
        Assert.NotEqual(Turn.None, placement.Turn);
        Assert.Equal(Ways("y+,y-"), bench.Runs.WaysOf(placement, Origin));
    }

    [Fact]
    public void A_modded_panel_swaps_to_the_game_window()
    {
        var bench = new SwapBench();
        var panel = bench.Real("ModPanel", Large(0, 0, 0));

        var drafts = bench.Drafts(Refs(panel), new SwapTo.SameShapeFrom(bench.Kit(Window)));

        var swap = Assert.IsType<Intent.Swap>(Assert.Single(drafts).Intent);
        Assert.Equal(new Placement(bench.Piece(Window), Large(0, 0, 0), Turn.None), swap.New);
    }

    [Fact]
    public void Exactly_a_piece_of_another_shape_is_refused_no_piece()
    {
        var bench = new SwapBench();
        var cable = bench.Real(Straight, Origin);

        var why = bench.Refusal(Refs(cable), new SwapTo.Exactly(bench.Piece(Corner)));

        Assert.Equal(ReasonCodes.NoPiece, why.Code);
        Assert.Equal(bench.Piece(Corner).Hash.ToString(), why.Facts["with"]);
        Assert.Equal("1", why.Facts["picked"]);
    }

    [Fact]
    public void A_long_straight_with_no_heavy_length_is_left_out_of_a_swap_all()
    {
        var bench = new SwapBench();
        var single = bench.Real(Straight, Origin);
        var longer = bench.Real("StructureCableStraight3", At(4, 0, 0));
        var heavy = new SwapTo.SameShapeFrom(bench.Kit(HeavyStraight));

        var both = bench.Drafts(Refs(single, longer), heavy);
        var alone = bench.Refusal(Refs(longer), heavy);

        Assert.Equal(single, Assert.IsType<Intent.Swap>(Assert.Single(both).Intent).Old);
        Assert.Equal(ReasonCodes.NoPiece, alone.Code);
        Assert.Equal(bench.Kit(HeavyStraight).Hash.ToString(), alone.Facts["kit"]);
    }

    [Fact]
    public void Structures_already_that_piece_get_no_ghost_and_no_refusal()
    {
        var bench = new SwapBench();
        var cable = bench.Real(Straight, Origin);

        Assert.Empty(bench.Drafts(Refs(cable), new SwapTo.SameShapeFrom(bench.Kit(Straight))));
        Assert.Empty(bench.Drafts(Refs(cable), new SwapTo.Exactly(bench.Piece(Straight))));
    }

    [Fact]
    public void A_selector_that_picks_nothing_gives_an_empty_place()
    {
        var bench = new SwapBench();

        Assert.Empty(bench.Drafts(Refs(), new SwapTo.SameShapeFrom(bench.Kit(HeavyStraight))));
    }

    [Fact]
    public void The_selector_refusal_passes_through()
    {
        var bench = new SwapBench();

        var why = bench.Refusal(new StructureSelector.Box(Origin, At(2, 2, 2)), new SwapTo.SameShapeFrom(bench.Kit(HeavyStraight)));

        Assert.Equal(ReasonCodes.UnknownKind, why.Code);
    }

    [Fact]
    public void An_unknown_kit_is_refused_with_the_catalogue_reason()
    {
        var bench = new SwapBench();
        var cable = bench.Real(Straight, Origin);

        var why = bench.Refusal(Refs(cable), new SwapTo.SameShapeFrom(new KitKey(12345)));

        Assert.True(bench.Runs.Catalogue.Kit(new KitKey(12345)).TryGetError(out var expected));
        Assert.Equal(expected.Code, why.Code);
    }

    [Fact]
    public void An_unknown_kind_of_swap_is_refused_unknown_kind()
    {
        var bench = new SwapBench();
        var cable = bench.Real(Straight, Origin);

        var why = bench.Refusal(Refs(cable), UnknownCase.Of<SwapTo>());

        Assert.Equal(ReasonCodes.UnknownKind, why.Code);
    }

    [Fact]
    public void Swap_ghosts_go_into_the_plan_asked_made_by_the_maker_asked()
    {
        var bench = new SwapBench();
        var cable = bench.Real(Straight, Origin);
        var plan = new PlanId(7);

        var draft = Assert.Single(bench.Drafts(Refs(cable), new SwapTo.SameShapeFrom(bench.Kit(HeavyStraight)), Option.Some(plan)));

        Assert.Equal(Option.Some(plan), draft.Plan);
        Assert.Equal(SwapBench.Asker, draft.MadeBy);
        Assert.IsType<Visibility.Everyone>(draft.Visibility);
    }

    [Fact]
    public void Swap_ghosts_are_made_by_the_maker_the_command_names()
    {
        var bench = new SwapBench();
        var cable = bench.Real(Straight, Origin);
        var command = new Command.PlanSwap(Refs(cable), new SwapTo.SameShapeFrom(bench.Kit(HeavyStraight)), Option<PlanId>.None, TestBoard.TestMaker);

        var work = new SwapExpander(bench.Planner).Start(new ExpansionInput<Command.PlanSwap>(
            command, new CommandId(Guid.NewGuid()), TestBoard.Alice, bench.Runs.Boards.View, bench.Runs.Query));
        while (work.Step() == StepOutcome.More)
        {
        }

        var draft = Assert.Single(Assert.IsType<Command.Place>(TestKitIds.Must(work.Found)).Ghosts);
        Assert.Equal(TestBoard.TestMaker, draft.MadeBy);
    }

    [Fact]
    public void Options_for_a_structure_reference_match_the_options_of_the_structure()
    {
        var bench = new SwapBench();
        var wall = bench.Real(Wall, Large(0, 0, 0));
        ISwaps swaps = bench.Planner;

        Assert.Equal(bench.Options(wall), TestKitIds.Must(swaps.OptionsFor(wall)));
    }

    [Fact]
    public void Options_for_a_structure_that_is_gone_are_refused_not_found()
    {
        var bench = new SwapBench();
        ISwaps swaps = bench.Planner;
        var gone = bench.Real(Wall, Large(0, 0, 0)) with { Id = new ReferenceId(987654) };

        Assert.True(swaps.OptionsFor(gone).TryGetError(out var why));
        Assert.Equal(ReasonCodes.NotFound, why.Code);
        Assert.Equal("987654", why.Facts["structure"]);
    }

    [Fact]
    public void A_long_swap_all_runs_in_slices()
    {
        var bench = new SwapBench();
        var cables = Enumerable.Range(0, 2 * SwapExpansion.PerStep + 2).Select(i => bench.Real(Straight, At(i, 0, 0))).ToArray();

        var (found, steps) = bench.Expand(Refs(cables), new SwapTo.SameShapeFrom(bench.Kit(HeavyStraight)));

        Assert.Equal(4, steps);
        Assert.Equal(cables.Length, Assert.IsType<Command.Place>(TestKitIds.Must(found)).Ghosts.Count);
    }
}
