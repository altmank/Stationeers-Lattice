using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

public sealed class PieceMapperTests
{
    private static readonly KitKey SomeKit = PrefabHash.Kit("ItemKitSome");
    private static readonly KitOption First = default;

    private static PieceFacts Map(PrefabData prefab) => Ok(PieceMapper.Map(prefab, ValueList.Of(new KitPlace(SomeKit, First))));

    [Fact]
    public void Key_and_kits_are_the_ones_given()
    {
        var option = Ok(KitOption.Create(3));
        var kits = ValueList.From(new[] { new KitPlace(SomeKit, option), new KitPlace(PrefabHash.Kit("ItemKitOther"), First) });
        var facts = Ok(PieceMapper.Map(Piece("StructureThing"), kits));

        Assert.Equal(PrefabHash.Piece("StructureThing"), facts.Key);
        Assert.Equal(kits, facts.Kits);
    }

    [Fact]
    public void A_piece_no_kit_places_is_described_with_no_kit()
    {
        var facts = Ok(PieceMapper.Map(Piece("StructureCableStraightBurnt"), ValueList<KitPlace>.Empty));

        Assert.Empty(facts.Kits);
        Assert.NotEmpty(facts.Steps);
    }

    [Fact]
    public void A_small_piece_takes_its_slot_and_collision_class_from_the_prefab()
    {
        var prefab = Piece("StructureCableStraight", run: RunKind.Cable) with { SmallSlot = Option.Some(Slot.Cable), Collision = 2 };

        var facts = Map(prefab);

        Assert.Equal(ValueList.Of(new FootprintCell(default, Slot.Cable)), facts.Footprint);
        Assert.Equal(SmallCollision.Cables, facts.Collision);
    }

    [Fact]
    public void A_long_small_piece_fills_every_small_cell_its_grid_bounds_snap_to_with_its_slot()
    {
        var prefab = Piece("StructureCableStraightLong3", run: RunKind.Cable) with
        {
            SmallSlot = Option.Some(Slot.Cable),
            SmallCells = ValueList.From(new[] { new CellOffset(0, 0, 0), new CellOffset(0, 0, -11), new CellOffset(0, 0, -6), new CellOffset(0, 0, -1) }),
        };

        Assert.Equal(
            new[] { new FootprintCell(default, Slot.Cable), new FootprintCell(new CellOffset(0, 0, -10), Slot.Cable), new FootprintCell(new CellOffset(0, 0, -5), Slot.Cable) },
            Map(prefab).Footprint);
    }

    [Fact]
    public void A_modded_small_device_fills_the_cells_its_prefab_names()
    {
        var prefab = Piece("ModdedWideDevice", run: RunKind.Other) with
        {
            SmallSlot = Option.Some(Slot.Device),
            SmallCells = ValueList.From(new[] { new CellOffset(5, 0, 0), new CellOffset(0, 0, 0) }),
        };

        Assert.Equal(
            new[] { new FootprintCell(default, Slot.Device), new FootprintCell(new CellOffset(5, 0, 0), Slot.Device) },
            Map(prefab).Footprint);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(-6, -5)]
    [InlineData(-11, -10)]
    [InlineData(4, 5)]
    [InlineData(7, 5)]
    [InlineData(-2, 0)]
    [InlineData(-3, -5)]
    public void Offsets_snap_to_the_games_small_cells(int raw, int cell) => Assert.Equal(cell, PieceMapper.SmallCell(raw));

    [Fact]
    public void A_state_that_blocks_gravity_says_so()
    {
        var steps = Map(Piece("StructureWall", grid: 2, states: new[] { State() with { BlocksGravity = true }, State() })).Steps;

        Assert.True(steps[0].BlocksGravity);
        Assert.False(steps[1].BlocksGravity);
    }

    [Theory]
    [InlineData((int)PipeContent.Gas)]
    [InlineData((int)PipeContent.Liquid)]
    [InlineData((int)PipeContent.Any)]
    public void A_pipe_keeps_what_it_holds(int content)
    {
        var prefab = Piece("StructurePipeStraight", run: RunKind.Pipe) with { PipeContent = Option.Some((PipeContent)content) };

        Assert.Equal(Option.Some((PipeContent)content), Map(prefab).Pipe);
    }

    [Fact]
    public void Only_a_pipe_holds_anything()
    {
        var prefab = Piece("StructureThing") with { PipeContent = Option.Some(PipeContent.Gas) };

        Assert.True(Map(prefab).Pipe.IsNone);
    }

    [Theory]
    [InlineData(2.0, 1)]
    [InlineData(0.5, 2)]
    public void Grid_comes_from_the_prefab_grid_size(double metres, int grid) =>
        Assert.Equal((GridSize)grid, Map(Piece("StructureThing", grid: metres)).Grid);

    [Fact]
    public void A_grid_size_the_game_does_not_use_is_refused_with_a_reason()
    {
        var reason = Error(PieceMapper.Map(Piece("StructureOdd", grid: 1.0), ValueList.Of(new KitPlace(SomeKit, First))));

        Assert.Equal(ReasonCodes.UnknownPiece, reason.Code);
        Assert.Equal("StructureOdd", reason.Facts["piece"]);
    }

    // BuildStates[i].Tool names ToolEntry, ToolEntry2 and ToolExit (Assets.Scripts.Objects/ToolBasic.cs, ToolUse.cs);
    // only a Stackable entry is used up (Assets.Scripts.Objects/Structure.cs:1661-1688).
    [Fact]
    public void A_stack_entry_is_the_material_and_the_other_entry_is_the_tool()
    {
        var facts = Map(Piece("StructureFrame", grid: 2, states: new[]
        {
            State(Stack("ItemSteelFrames", 1), exit: "ItemAngleGrinder"),
            State(Tool("ItemWeldingTorch"), Stack("ItemSteelSheets", 1), exit: "ItemWrench"),
            State(Tool("ItemWeldingTorch"), Stack("ItemSteelSheets", 1), exit: "ItemWrench", air: true),
            State(Tool("ItemScrewdriver"), Stack("ItemPlasticSheets", 1), exit: "ItemCrowbar", air: true),
        }));

        Assert.Equal(4, facts.Steps.Count);
        var kit = facts.Steps[0];
        Assert.True(kit.EntryTool.IsNone);
        Assert.Equal(Ok(ItemQuantity.Create(PrefabHash.Item("ItemSteelFrames"), 1)), kit.Material.Reduce(default(ItemQuantity)));
        Assert.Equal(PrefabHash.Item("ItemAngleGrinder"), kit.ExitTool.Reduce(default(ItemKey)));
        var weld = facts.Steps[2];
        Assert.Equal(PrefabHash.Item("ItemWeldingTorch"), weld.EntryTool.Reduce(default(ItemKey)));
        Assert.Equal(PrefabHash.Item("ItemSteelSheets"), weld.Material.Reduce(default(ItemQuantity)).Item);
        Assert.Equal(new[] { false, false, true, true }, Air(facts));
        for (var i = 0; i < facts.Steps.Count; i++)
            Assert.Equal(i, facts.Steps[i].Index.Value);
    }

    [Fact]
    public void A_tool_with_a_use_quantity_is_still_a_tool()
    {
        var step = Map(Piece("StructureFairing", states: new[]
        {
            State(Tool("ItemWeldingTorch", 500), Stack("ItemSteelSheets", 2), exit: "ItemWrench"),
        })).Steps[0];

        Assert.Equal(PrefabHash.Item("ItemWeldingTorch"), step.EntryTool.Reduce(default(ItemKey)));
        Assert.Equal(2, step.Material.Reduce(default(ItemQuantity)).Count);
    }

    [Fact]
    public void A_kit_with_an_offhand_tool_gives_both()
    {
        var step = Map(Piece("StructurePipeStraight", states: new[]
        {
            State(Stack("ItemKitPipe", 1), Tool("ItemWrench"), exit: "ItemWrench"),
        })).Steps[0];

        Assert.Equal(PrefabHash.Item("ItemWrench"), step.EntryTool.Reduce(default(ItemKey)));
        Assert.Equal(PrefabHash.Item("ItemKitPipe"), step.Material.Reduce(default(ItemQuantity)).Item);
    }

    // Structure.HandleToolUse destroys a non-stack item held in the other hand when ToolEntry2 has a quantity: the
    // printer mods of StructureAutolathe and three other printers (live check, test instance).
    [Fact]
    public void An_offhand_item_with_a_quantity_is_used_up()
    {
        var step = Map(Piece("StructureAutolathe", states: new[]
        {
            State(Tool("ItemScrewdriver"), Tool("AutolathePrinterMod", 1), exit: "ItemDrill"),
        })).Steps[0];

        Assert.Equal(PrefabHash.Item("ItemScrewdriver"), step.EntryTool.Reduce(default(ItemKey)));
        Assert.Equal(Ok(ItemQuantity.Create(PrefabHash.Item("AutolathePrinterMod"), 1)), step.Material.Reduce(default(ItemQuantity)));
    }

    [Fact]
    public void A_held_tool_with_a_use_quantity_in_the_main_hand_is_not_used_up()
    {
        var step = Map(Piece("StructureThing", states: new[] { State(Tool("ItemWeldingTorch", 1000)) })).Steps[0];

        Assert.True(step.Material.IsNone);
    }

    [Fact]
    public void A_state_with_no_entry_is_a_step_with_nothing_needed()
    {
        var step = Map(Piece("StructureBlocker", states: new[] { State() })).Steps[0];

        Assert.True(step.EntryTool.IsNone);
        Assert.True(step.Material.IsNone);
        Assert.True(step.ExitTool.IsNone);
    }

    [Fact]
    public void A_stack_entry_with_no_quantity_is_held_not_used()
    {
        var step = Map(Piece("StructureThing", states: new[] { State(Stack("ItemIronSheets", 0)) })).Steps[0];

        Assert.Equal(PrefabHash.Item("ItemIronSheets"), step.EntryTool.Reduce(default(ItemKey)));
        Assert.True(step.Material.IsNone);
    }

    [Fact]
    public void Two_materials_in_one_step_are_refused_with_a_reason()
    {
        var reason = Error(PieceMapper.Map(
            Piece("StructureTwo", states: new[] { State(Stack("ItemIronSheets", 1), Stack("ItemGlassSheets", 1)) }),
            ValueList.Of(new KitPlace(SomeKit, First))));

        Assert.Equal(ReasonCodes.UnknownPiece, reason.Code);
        Assert.Contains("material", reason.Facts["problem"]);
    }

    [Fact]
    public void No_build_state_is_refused_with_a_reason() =>
        Assert.Equal(ReasonCodes.UnknownPiece, Error(PieceMapper.Map(Piece("StructureEmpty", states: new StateData[0]), ValueList.Of(new KitPlace(SomeKit, First)))).Code);

    // A cable end sits 0.30-0.40 m from the centre, just inside the neighbour cell, facing back;
    // LocalGrid = the end's cell, FacingGrid = 0.5 m along
    // its forward (Assets.Scripts.Objects/Connection.cs SetGrids).
    [Fact]
    public void A_cable_end_leaves_its_own_cell_outward()
    {
        var facts = Map(Piece("StructureCableStraight", run: RunKind.Cable, cableWatts: 5000, ends: new[]
        {
            End(new Vector(0, 0, 0.40), new Vector(0, 0, -1), NetworkKinds.Power | NetworkKinds.Data),
            End(new Vector(0, 0, -0.34), new Vector(0, 0, 1), NetworkKinds.Power | NetworkKinds.Data),
        }));

        Assert.Equal(
            new[]
            {
                new ConnectionEnd(new CellOffset(0, 0, 0), Direction.ZPlus, EndKinds.Cable) { Reach = Option.Some(new Millimetres(400)) },
                new ConnectionEnd(new CellOffset(0, 0, 0), Direction.ZMinus, EndKinds.Cable) { Reach = Option.Some(new Millimetres(340)) },
            },
            facts.Ends);
    }

    // Long straights: pivot plus N-1 cells along local -z, ends +z at the pivot and -z at the far cell.
    [Fact]
    public void A_long_straight_end_leaves_from_its_far_cell()
    {
        var facts = Map(Piece("StructureCableStraight3", run: RunKind.Cable, ends: new[]
        {
            End(new Vector(0, 0, 0.4), new Vector(0, 0, -1), NetworkKinds.Power | NetworkKinds.Data),
            End(new Vector(0, 0, -1.4), new Vector(0, 0, 1), NetworkKinds.Power | NetworkKinds.Data),
        }));

        Assert.Equal(new ConnectionEnd(new CellOffset(0, 0, -10), Direction.ZMinus, EndKinds.Cable) { Reach = Option.Some(new Millimetres(400)) }, facts.Ends[1]);
    }

    [Theory]
    [InlineData((int)RunKind.Pipe, (int)NetworkKinds.Pipe, "gas")]
    [InlineData((int)RunKind.Pipe, (int)NetworkKinds.PipeLiquid, "liquid")]
    [InlineData((int)RunKind.Chute, (int)NetworkKinds.Chute, "chute")]
    [InlineData((int)RunKind.Other, (int)NetworkKinds.Power, "power")]
    [InlineData((int)RunKind.Cable, (int)(NetworkKinds.Power | NetworkKinds.Data), "power,data")]
    [InlineData((int)RunKind.Other, (int)NetworkKinds.Elevator, "elevator")]
    [InlineData((int)RunKind.Other, (int)NetworkKinds.RoboticArmRail, "rail")]
    public void An_end_carries_the_game_kinds_it_has(int run, int kinds, string expected)
    {
        var facts = Map(Piece("StructureThing", run: (RunKind)run, ends: new[]
        {
            End(new Vector(-0.4, 0, 0), new Vector(1, 0, 0), (NetworkKinds)kinds),
        }));

        var end = Assert.Single(facts.Ends);
        Assert.Equal((new CellOffset(0, 0, 0), Direction.XMinus), (end.At, end.Facing));
        Assert.Equal(expected, CatalogueText.Kinds(end.Kinds));
    }

    [Fact]
    public void An_end_of_a_modded_network_is_kept_with_its_game_name_and_bits()
    {
        var facts = Map(Piece("StructureModConduit", ends: new[]
        {
            End(new Vector(0, 0.4, 0), new Vector(0, -1, 0), (NetworkKinds)(0x200 | (int)NetworkKinds.Power)) with { KindName = "Hydrogen, Power" },
        }));

        var kinds = Assert.Single(facts.Ends).Kinds;
        Assert.True(kinds.Contains(new NetworkType.Power()));
        Assert.True(kinds.Contains(new NetworkType.Unknown("Hydrogen, Power", 0x200)));
    }

    [Fact]
    public void An_end_with_no_kind_is_left_out()
    {
        var facts = Map(Piece("StructureThing", ends: new[] { End(new Vector(0, 0.4, 0), new Vector(0, -1, 0), NetworkKinds.None) }));

        Assert.Empty(facts.Ends);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, (int)ConnectionRole.Input)]
    [InlineData(3, (int)ConnectionRole.Output)]
    [InlineData(5, (int)ConnectionRole.Waste)]
    public void An_end_keeps_its_device_role(int gameRole, int? role)
    {
        var facts = Map(Piece("StructureMachine", ends: new[] { End(new Vector(-0.4, 0, 0), new Vector(1, 0, 0), NetworkKinds.Pipe) with { Role = gameRole } }));

        Assert.Equal(role is { } r ? Option.Some((ConnectionRole)r) : Option<ConnectionRole>.None, Assert.Single(facts.Ends).Role);
    }

    [Fact]
    public void A_device_port_on_a_large_piece_is_offset_in_decimetres()
    {
        var facts = Map(Piece("StructureMachine", grid: 2, ends: new[]
        {
            End(new Vector(1.4, -0.5, 0), new Vector(-1, 0, 0), NetworkKinds.Power),
        }));

        Assert.Equal(new ConnectionEnd(new CellOffset(10, -5, 0), Direction.XPlus, EndKinds.Power) { Reach = Option.Some(new Millimetres(400)) }, Assert.Single(facts.Ends));
    }

    [Fact]
    public void An_end_whose_forward_is_not_along_an_axis_is_refused_with_a_reason()
    {
        var reason = Error(PieceMapper.Map(
            Piece("StructureSkew", ends: new[] { End(new Vector(0.3, 0, 0.3), new Vector(-0.7071, 0, -0.7071), NetworkKinds.Pipe) }),
            ValueList.Of(new KitPlace(SomeKit, First))));

        Assert.Contains("end", reason.Facts["problem"]);
    }

    // Cable.MaxVoltage, Pipe.MaxPressure, Structure.maxPressureDelta with -1 for no limit.
    [Fact]
    public void Ratings_come_from_the_prefab_and_no_limit_is_none()
    {
        var cable = Map(Piece("StructureCableStraightH", run: RunKind.Cable, cableWatts: 100000)).Ratings;
        var pipe = Map(Piece("StructurePipeStraight", run: RunKind.Pipe, pipeKilopascals: 60795)).Ratings;
        var wall = Map(Piece("StructureWallIron", grid: 2, maxPressureDelta: 200)).Ratings;
        var frame = Map(Piece("StructureFrame", grid: 2, maxPressureDelta: -1)).Ratings;

        Assert.Equal(new Ratings(Option.Some(new Watts(100000)), Option<Kilopascals>.None, Option<Kilopascals>.None), cable);
        Assert.Equal(Option.Some(new Kilopascals(60795)), pipe.Pipe);
        Assert.Equal(Option.Some(new Kilopascals(200)), wall.Face);
        Assert.Equal(new Ratings(Option<Watts>.None, Option<Kilopascals>.None, Option<Kilopascals>.None), frame);
    }

    [Fact]
    public void The_support_group_comes_from_the_class_chain() =>
        Assert.Equal(
            Option.Some(SupportGroups.FrameBelow),
            Map(Piece("StructureArcFurnace", grid: 2, types: new[] { Type("Assets.Scripts.Objects.Pipes.ArcFurnace", true), Type("Assets.Scripts.Objects.Pipes.Device", true), Type("Assets.Scripts.Objects.Structure", true) })).SupportGroup);

    private static bool[] Air(PieceFacts facts)
    {
        var air = new bool[facts.Steps.Count];
        for (var i = 0; i < air.Length; i++)
            air[i] = facts.Steps[i].BlocksAir;
        return air;
    }
}
