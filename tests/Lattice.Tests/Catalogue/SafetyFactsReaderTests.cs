using System;
using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// The reader finds the build safety facts by the piece's type, so a modded subclass inherits them: what a device keeps
/// going (decompile <c>Assets.Scripts.Objects.Pipes/IPoweredVent.cs</c>, <c>FiltrationMachineBase.cs</c>,
/// <c>IndustrialFiltration.cs</c>; <c>Assets.Scripts.Objects.Electrical/WallHeater.cs</c>, <c>WallCooler.cs</c>,
/// <c>AirConditioner.cs</c>), whether it supplies power (an override of <c>Device.GetGeneratedPower</c>,
/// <c>Assets.Scripts.Objects.Pipes/Device.cs</c>), whether it vents (<c>PassiveVent.cs</c>) and the side its controls
/// sit on (<c>Assets.Scripts.Objects/Thing.cs</c> <c>Interactables</c>).
/// </summary>
public sealed class SafetyFactsReaderTests
{
    private const string Structure = "Assets.Scripts.Objects.Structure";
    private const string Device = "Assets.Scripts.Objects.Pipes.Device";
    private const string Pipe = "Assets.Scripts.Objects.Pipes.Pipe";
    private const string PassiveVent = "Assets.Scripts.Objects.Pipes.PassiveVent";

    public static TheoryData<string, string[], string> KeepChains() => new()
    {
        { "StructureActiveVent", new[] { "Assets.Scripts.Objects.Pipes.ActiveVent", Device, Structure, "Assets.Scripts.Objects.Pipes.IPoweredVent" }, "air" },
        { "StructureFiltration", new[] { "Assets.Scripts.Objects.Pipes.FiltrationMachine", "Assets.Scripts.Objects.Pipes.FiltrationMachineBase", Device, Structure }, "air" },
        { "StructureFiltrationIndustrial", new[] { "Assets.Scripts.Objects.Pipes.IndustrialFiltration", Device, Structure }, "air" },
        { "StructureWallHeater", new[] { "Assets.Scripts.Objects.Electrical.WallHeater", Device, Structure }, "heat" },
        { "StructureWallCooler", new[] { "Assets.Scripts.Objects.Electrical.WallCooler", Device, Structure }, "heat" },
        { "StructureAirConditioner", new[] { "Assets.Scripts.Objects.Electrical.AirConditioner", Device, Structure }, "heat" },
        { "ModScrubber", new[] { "SomeMod.Scrubber", "Assets.Scripts.Objects.Pipes.FiltrationMachineBase", Device, Structure }, "air" },
        { "ModFanHeater", new[] { "SomeMod.FanHeater", Device, Structure, "Assets.Scripts.Objects.Pipes.IPoweredVent", "SomeMod.IHeats" }, "air" },
        { "StructureWallLight", new[] { "Assets.Scripts.Objects.Electrical.WallLight", Device, Structure }, "none" },
        { "ModThing", new[] { "SomeMod.Thing", Structure }, "none" },
    };

    [Theory]
    [MemberData(nameof(KeepChains))]
    public void What_a_device_keeps_going_follows_its_type(string name, string[] chain, string expected)
    {
        var facts = Map(Piece(name, grid: 2, types: Array.ConvertAll(chain, t => Type(t))));

        Assert.Equal(expected, facts.Keeps == Keeps.None ? "none" : CatalogueText.Keeps(facts.Keeps));
    }

    [Fact]
    public void A_piece_whose_type_overrides_the_generated_power_supplies_power_a_modded_one_too()
    {
        var battery = Map(Piece("StructureBattery", grid: 2, types: new[]
        {
            Type("Assets.Scripts.Objects.Electrical.Battery"),
            Type("Assets.Scripts.Objects.Electrical.ElectricalInputOutput") with { DeclaresGeneratedPower = true },
            Type(Device),
            Type(Structure),
        }));
        var modded = Map(Piece("ModReactor", grid: 2, types: new[]
        {
            Type("SomeMod.Reactor") with { DeclaresGeneratedPower = true },
            Type(Device),
            Type(Structure),
        }));
        var light = Map(Piece("StructureWallLight", grid: 0.5, types: new[] { Type(Device), Type(Structure) }));

        Assert.True(battery.Supplies);
        Assert.True(modded.Supplies);
        Assert.False(light.Supplies);
    }

    [Fact]
    public void A_passive_vent_and_its_subclasses_vent_a_plain_pipe_does_not()
    {
        var vent = Map(Piece("StructurePassiveVent", run: RunKind.Pipe, types: new[] { Type(PassiveVent), Type(Pipe), Type(Structure) }));
        var gasVent = Map(Piece("StructurePipeVent", run: RunKind.Pipe, types: new[] { Type("Assets.Scripts.Objects.Pipes.GasPipeVent"), Type(PassiveVent), Type(Pipe) }));
        var modded = Map(Piece("ModVent", run: RunKind.Pipe, types: new[] { Type("SomeMod.Vent"), Type(PassiveVent), Type(Pipe) }));
        var pipe = Map(Piece("StructurePipeStraight", run: RunKind.Pipe, types: new[] { Type(Pipe), Type(Structure) }));

        Assert.True(vent.Vents);
        Assert.True(gasVent.Vents);
        Assert.True(modded.Vents);
        Assert.False(pipe.Vents);
    }

    [Fact]
    public void The_control_face_is_the_side_most_controls_sit_on()
    {
        var facts = Map(Piece("ModConsole", grid: 2) with
        {
            Controls = ValueList.From(new[] { new Vector(0.1, 0.2, 0.9), new Vector(-0.2, 0.0, 0.8), new Vector(0.9, 0.0, 0.1) }),
        });

        Assert.Equal(Option.Some(Direction.ZPlus), facts.ControlFace);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0, "none")]
    [InlineData(-0.6, 0.1, 0.0, "x-")]
    [InlineData(0.0, -0.4, 0.1, "y-")]
    public void One_control_names_the_side_it_sits_on_and_one_at_the_pivot_names_none(double x, double y, double z, string expected)
    {
        var facts = Map(Piece("ModButton", grid: 2) with { Controls = ValueList.From(new[] { new Vector(x, y, z) }) });

        Assert.Equal(expected, facts.ControlFace.Map(static d => CatalogueText.Direction(d)).Reduce("none"));
    }

    [Fact]
    public void Controls_split_evenly_between_two_sides_name_no_side()
    {
        var facts = Map(Piece("ModPanel", grid: 2) with
        {
            Controls = ValueList.From(new[] { new Vector(0.0, 0.0, 0.9), new Vector(0.0, 0.0, -0.9) }),
        });

        Assert.True(facts.ControlFace.IsNone);
    }

    [Fact]
    public void A_piece_with_no_controls_has_no_control_face()
    {
        Assert.True(Map(Piece("StructureWallIron", grid: 2)).ControlFace.IsNone);
    }

    [Fact]
    public void The_safety_facts_the_reader_finds_survive_the_catalogue_file()
    {
        var heater = Piece("ModFanHeater", grid: 0.5, types: new[] { Type("SomeMod.FanHeater"), Type("Assets.Scripts.Objects.Electrical.WallHeater"), Type(Structure, true) }) with
        {
            Controls = ValueList.From(new[] { new Vector(0.0, 0.0, -0.2) }),
        };
        var reactor = Piece("ModReactor", grid: 2, types: new[] { Type("SomeMod.Reactor") with { DeclaresGeneratedPower = true }, Type(Structure, true) });
        var vent = Piece("ModVent", run: RunKind.Pipe, types: new[] { Type("SomeMod.Vent"), Type(PassiveVent), Type(Structure, true) });
        var catalogue = new PrefabCatalogue(new CountingSource(new[] { Kit("ItemKitModSafety", heater, reactor, vent) }, new[] { heater, reactor, vent }));
        var text = new StringWriter();
        CatalogueDataFile.Write(catalogue, catalogue.ReadAll(), TimeSpan.FromMilliseconds(1), text);

        var read = CatalogueFile.Read(new StringReader(text.ToString()), "safety.lcat").TryGet(out var file) ? file : throw new InvalidOperationException(text.ToString());

        Assert.Equal(Keeps.Heat, read.Named("ModFanHeater").Keeps);
        Assert.Equal(Option.Some(Direction.ZMinus), read.Named("ModFanHeater").ControlFace);
        Assert.True(read.Named("ModReactor").Supplies);
        Assert.True(read.Named("ModVent").Vents);
        Assert.False(read.Named("ModVent").Supplies);
    }

    [GameCatalogueFact]
    public void The_games_own_catalogue_carries_the_safety_facts_by_type()
    {
        var game = GameCatalogue.Load();

        Assert.Equal(Keeps.Air, game.Named("StructureActiveVent").Keeps);
        Assert.Equal(Keeps.Air, game.Named("StructureFiltration").Keeps);
        Assert.Equal(Keeps.Heat, game.Named("StructureWallHeater").Keeps);
        Assert.True(game.Named("StructureBattery").Supplies);
        Assert.True(game.Named("StructureSolarPanel").Supplies);
        Assert.True(game.Named("StructureAreaPowerControl").Supplies);
        Assert.True(game.Named("StructurePassiveVent").Vents);
        Assert.False(game.Named("StructurePipeStraight").Vents);
        Assert.True(game.Named("StructureWallHeater").ControlFace.IsSome);
        Assert.True(game.Named("StructureFrame").ControlFace.IsNone);
        Assert.Equal(Keeps.None, game.Named("StructureFrame").Keeps);
    }

    private static PieceFacts Map(PrefabData prefab) =>
        PieceMapper.Map(prefab, ValueList<KitPlace>.Empty).TryGet(out var facts) ? facts : throw new InvalidOperationException(prefab.Name);
}
