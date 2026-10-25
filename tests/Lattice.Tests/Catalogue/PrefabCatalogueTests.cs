using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Contracts;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

internal static class SampleGame
{
    public static readonly PrefabData Frame = Piece("StructureFrame", grid: 2, types: new[] { Type("Objects.Structures.Frame"), Type("Assets.Scripts.Objects.LargeStructure"), Type("Assets.Scripts.Objects.Structure", true) }, states: new[]
    {
        State(Stack("ItemSteelFrames", 1), exit: "ItemAngleGrinder"),
        State(Tool("ItemWeldingTorch"), Stack("ItemSteelSheets", 1), exit: "ItemWrench"),
        State(Tool("ItemWeldingTorch"), Stack("ItemSteelSheets", 1), exit: "ItemWrench", air: true),
        State(Tool("ItemScrewdriver"), Stack("ItemPlasticSheets", 1), exit: "ItemCrowbar", air: true),
    }) with { Look = Option.Some(new PrefabLook(Option.Some(new PieceBox(new Vector(0, 0, 0), new Vector(2, 2, 2))), 3, 2, 1, Option<bool>.None)) };

    public static readonly PrefabData Straight = Piece("StructureCableStraight", run: RunKind.Cable, cableWatts: 5000, states: new[]
    {
        State(Stack("ItemCableCoil", 1), exit: "ItemWireCutters"),
    }, ends: new[]
    {
        End(new Vector(0, 0, 0.4), new Vector(0, 0, -1), NetworkKinds.Power | NetworkKinds.Data),
        End(new Vector(0, 0, -0.4), new Vector(0, 0, 1), NetworkKinds.Power | NetworkKinds.Data),
    }) with { Look = Option.Some(new PrefabLook(Option.Some(new PieceBox(new Vector(0, 0.1, 0), new Vector(0.1, 0.1, 0.5))), 0, 0, 0, Option.Some(true))) };

    public static readonly PrefabData Corner = Piece("StructureCableCorner", run: RunKind.Cable, cableWatts: 5000, states: new[]
    {
        State(Stack("ItemCableCoil", 1), exit: "ItemWireCutters"),
    }, ends: new[]
    {
        End(new Vector(-0.4, 0, 0), new Vector(1, 0, 0), NetworkKinds.Power | NetworkKinds.Data),
        End(new Vector(0, 0, 0.4), new Vector(0, 0, -1), NetworkKinds.Power | NetworkKinds.Data),
    }) with { Look = Option.Some(new PrefabLook(Option<PieceBox>.None, 0, 0, 0, Option<bool>.None)) };

    public static readonly PrefabData Furnace = Piece("StructureArcFurnace", grid: 2, types: new[] { Type("Assets.Scripts.Objects.Pipes.ArcFurnace", true), Type("Assets.Scripts.Objects.Pipes.Device", true), Type("Assets.Scripts.Objects.SmallGrid", true), Type("Assets.Scripts.Objects.Structure", true) }, maxPressureDelta: -1)
        with { Look = Option.Some(new PrefabLook(Option.Some(new PieceBox(new Vector(0.25, 1, 0), new Vector(2, 2, 1.5))), 0, 0, 0, Option<bool>.None)) };

    public static readonly PrefabData Odd = Piece("StructureOdd", grid: 1.0);

    public static readonly PrefabData Burnt = Piece("StructureCableStraightBurnt", run: RunKind.Cable);

    public static readonly PrefabData Sensor = Piece("StructureGasSensor", states: new[] { State(Stack("ItemKitSensor", 1)) });

    public static CountingSource Source() => new(
        new[]
        {
            Kit("ItemSteelFrames", Frame),
            Kit("ItemCableCoil", Straight, Corner),
            Kit("ItemKitFurnaces", Furnace),
            Kit("ItemGasSensor", Sensor),
            Kit("ItemKitSensor", Odd, Furnace, Sensor),
        },
        new[] { Frame, Straight, Corner, Furnace, Odd, Burnt, Sensor });
}

public sealed class PrefabCatalogueTests
{
    [Fact]
    public void Kits_come_in_the_game_order()
    {
        var catalogue = new PrefabCatalogue(SampleGame.Source());

        Assert.Equal(PrefabHash.Kit("ItemSteelFrames"), catalogue.Kits[0]);
        Assert.Equal(5, catalogue.Kits.Count);
    }

    [Fact]
    public void A_kit_lists_its_pieces_in_option_order()
    {
        var catalogue = new PrefabCatalogue(SampleGame.Source());
        var coil = PrefabHash.Kit("ItemCableCoil");

        Assert.Equal(new[] { SampleGame.Straight.Key, SampleGame.Corner.Key }, Ok(catalogue.Kit(coil)).Pieces);
        Assert.Equal(SampleGame.Corner.Key, Ok(catalogue.PieceOf(coil, Ok(KitOption.Create(1)))));
        Assert.Equal(Ok(KitOption.Create(1)), Ok(catalogue.Piece(SampleGame.Corner.Key)).Kits[0].Option);
    }

    [Fact]
    public void Each_prefab_is_read_once_and_kept()
    {
        var source = SampleGame.Source();
        var catalogue = new PrefabCatalogue(source);

        var first = Ok(catalogue.Piece(SampleGame.Frame.Key));
        var second = Ok(catalogue.Piece(SampleGame.Frame.Key));

        Assert.Same(first, second);
        Assert.Equal(1, source.Reads(SampleGame.Frame.Key));
    }

    [Fact]
    public void A_missing_prefab_is_a_result_and_is_asked_once()
    {
        var source = SampleGame.Source();
        var catalogue = new PrefabCatalogue(source);
        var absent = PrefabHash.Piece("StructureNotInTheGame");

        var reason = Error(catalogue.Piece(absent));
        Error(catalogue.Piece(absent));

        Assert.Equal(ReasonCodes.UnknownPiece, reason.Code);
        Assert.Equal(CatalogueReasons.Catalogue, reason.From);
        Assert.Equal(absent.Hash.ToString(), reason.Facts["piece"]);
        Assert.Equal(1, source.Reads(absent));
    }

    [Fact]
    public void A_structure_no_kit_places_is_described_with_no_kit()
    {
        var burnt = Ok(new PrefabCatalogue(SampleGame.Source()).Piece(SampleGame.Burnt.Key));

        Assert.Empty(burnt.Kits);
        Assert.NotEmpty(burnt.Steps);
    }

    [Fact]
    public void A_piece_the_mapper_refuses_stays_refused_with_the_mapper_reason()
    {
        var source = SampleGame.Source();
        var catalogue = new PrefabCatalogue(source);

        Assert.Equal("StructureOdd", Error(catalogue.Piece(SampleGame.Odd.Key)).Facts["piece"]);
        Assert.True(catalogue.Piece(SampleGame.Odd.Key).IsError);
        Assert.Equal(1, source.Reads(SampleGame.Odd.Key));
    }

    // Structure.cs:1278: the game takes a structure's kit from BuildStates[0].Tool.ToolEntry.
    [Fact]
    public void A_piece_in_two_kits_belongs_to_the_kit_its_first_step_uses()
    {
        var catalogue = new PrefabCatalogue(SampleGame.Source());

        var sensor = Ok(catalogue.Piece(SampleGame.Sensor.Key));

        Assert.Equal(PrefabHash.Kit("ItemKitSensor"), sensor.Kits[0].Kit);
        Assert.Equal(2, sensor.Kits[0].Option.Index);
        Assert.Equal(2, sensor.Kits.Count);
    }

    [Fact]
    public void A_piece_in_two_kits_whose_first_step_names_neither_belongs_to_the_first_kit()
    {
        var furnace = Ok(new PrefabCatalogue(SampleGame.Source()).Piece(SampleGame.Furnace.Key));

        Assert.Equal(PrefabHash.Kit("ItemKitFurnaces"), furnace.Kits[0].Kit);
        Assert.Equal(0, furnace.Kits[0].Option.Index);
    }

    [Fact]
    public void Read_all_maps_every_kit_piece_once_and_lists_the_refusals()
    {
        var source = SampleGame.Source();
        var catalogue = new PrefabCatalogue(source);

        var report = catalogue.ReadAll();

        Assert.Equal(6, report.Read);
        Assert.Equal(new[] { "StructureCableStraightBurnt" }, report.WithoutKit);
        var refused = Assert.Single(report.Refused);
        Assert.Equal("StructureOdd", refused.Facts["piece"]);
        Assert.Equal(1, source.Reads(SampleGame.Sensor.Key));
    }

    [Fact]
    public void Unknown_kits_and_options_are_refused()
    {
        var catalogue = new PrefabCatalogue(SampleGame.Source());

        Assert.Equal(ReasonCodes.UnknownPiece, Error(catalogue.Kit(PrefabHash.Kit("ItemNothing"))).Code);
        Assert.True(catalogue.PieceOf(PrefabHash.Kit("ItemCableCoil"), Ok(KitOption.Create(2))).IsError);
    }

    [Fact]
    public void Names_are_kept_for_the_data_file()
    {
        var catalogue = new PrefabCatalogue(SampleGame.Source());
        Assert.True(catalogue.NameOf(SampleGame.Corner.Key).IsNone);

        catalogue.Piece(SampleGame.Corner.Key);

        Assert.Equal("StructureCableCorner", catalogue.NameOf(SampleGame.Corner.Key).Reduce(""));
        Assert.Equal("ItemCableCoil", catalogue.NameOf(PrefabHash.Kit("ItemCableCoil")).Reduce(""));
    }
}

/// <summary>A mod's pieces: a kit the base game does not have, a class the census does not know.</summary>
public sealed class ModdedPieceTests
{
    private static readonly PrefabData Shelf = Piece(
        "StructureMyModWallShelf",
        grid: 0.5,
        types: new[]
        {
            Type("MyMod.Shelves.WallShelf", declares: true),
            Type("Assets.Scripts.Objects.Pipes.Device", declares: true),
            Type("Assets.Scripts.Objects.SmallGrid", declares: true),
            Type("Assets.Scripts.Objects.Structure", declares: true),
        },
        states: new[]
        {
            State(Stack("ItemKitMyModShelves", 2), exit: "ItemWrench"),
            State(Tool("ItemScrewdriver"), Stack("ItemMyModBrackets", 4), exit: "ItemScrewdriver", air: false),
        },
        ends: new[] { End(new Vector(0, -0.4, 0), new Vector(0, 1, 0), NetworkKinds.Power) },
        maxPressureDelta: 75);

    private static readonly PrefabData Furnace = Piece(
        "StructureMyModHoverFurnace",
        grid: 2,
        types: new[]
        {
            Type("MyMod.Machines.HoverFurnace"),
            Type("Assets.Scripts.Objects.Pipes.ArcFurnace", declares: true),
            Type("Assets.Scripts.Objects.Structure", declares: true),
        });

    private static PrefabCatalogue Catalogue() => new(new CountingSource(
        new[] { Kit("ItemCableCoil", SampleGame.Straight), Kit("ItemKitMyModShelves", Shelf, Furnace) },
        new[] { SampleGame.Straight, Shelf, Furnace }));

    [Fact]
    public void A_kit_the_base_game_does_not_have_is_listed_with_its_pieces()
    {
        var catalogue = Catalogue();
        var kit = PrefabHash.Kit("ItemKitMyModShelves");

        Assert.Contains(kit, catalogue.Kits);
        Assert.Equal(Furnace.Key, Ok(catalogue.PieceOf(kit, Ok(KitOption.Create(1)))));
    }

    [Fact]
    public void A_modded_piece_is_read_from_its_own_prefab_data()
    {
        var shelf = Ok(Catalogue().Piece(Shelf.Key));

        Assert.Equal(PrefabHash.Kit("ItemKitMyModShelves"), shelf.Kits[0].Kit);
        Assert.Equal(GridSize.Small, shelf.Grid);
        Assert.Equal(2, shelf.Steps.Count);
        Assert.Equal(Ok(ItemQuantity.Create(PrefabHash.Item("ItemMyModBrackets"), 4)), shelf.Steps[1].Material.Reduce(default(ItemQuantity)));
        var end = Assert.Single(shelf.Ends);
        Assert.Equal((new CellOffset(0, 0, 0), Direction.YMinus), (end.At, end.Facing));
        Assert.Equal(Option.Some(new Kilopascals(75)), shelf.Ratings.Face);
    }

    [Fact]
    public void A_modded_class_with_its_own_placement_check_asks_the_game() =>
        Assert.Equal(Option.Some(SupportGroups.AskGame), Ok(Catalogue().Piece(Shelf.Key)).SupportGroup);

    [Fact]
    public void A_modded_subclass_of_a_census_class_takes_its_group() =>
        Assert.Equal(Option.Some(SupportGroups.FrameBelow), Ok(Catalogue().Piece(Furnace.Key)).SupportGroup);

    [Fact]
    public void A_modded_piece_survives_the_data_file()
    {
        var catalogue = Catalogue();
        var text = new StringWriter();
        CatalogueDataFile.Write(catalogue, catalogue.ReadAll(), TimeSpan.Zero, text);

        var read = Ok(CatalogueFile.Read(new StringReader(text.ToString()), "modded.lcat"));

        Assert.Equal(Ok(catalogue.Piece(Shelf.Key)), read.Named("StructureMyModWallShelf"));
    }
}

/// <summary>The shared catalogue suite over the prefab catalogue (pieces in one kit each, all mappable).</summary>
public sealed class PrefabCatalogueSuite : CatalogueContract
{
    internal override ICatalogue Subject { get; } = new PrefabCatalogue(new CountingSource(
        new[] { Kit("ItemSteelFrames", SampleGame.Frame), Kit("ItemCableCoil", SampleGame.Straight, SampleGame.Corner) },
        new[] { SampleGame.Frame, SampleGame.Straight, SampleGame.Corner }));
}
