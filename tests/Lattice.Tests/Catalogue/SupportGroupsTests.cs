using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

// The game's placement checks by class; full type names from the decompile (the class files named below).
public sealed class SupportGroupsTests
{
    private const string Structure = "Assets.Scripts.Objects.Structure";
    private const string SmallGrid = "Assets.Scripts.Objects.SmallGrid";
    private const string Device = "Assets.Scripts.Objects.Pipes.Device";

    public static TheoryData<string, string> Groups => new()
    {
        { "Assets.Scripts.Objects.Electrical.Battery", "lattice.support.frame-below" },
        { "Assets.Scripts.LargeElectrical", "lattice.support.frame-below" },
        { "Assets.Scripts.Objects.Flag", "lattice.support.frame-or-ground" },
        { "Assets.Scripts.Objects.Pipes.DeepMiner", "lattice.support.ground" },
        { "Objects.Structures.RocketTower", "lattice.support.frame-or-tower" },
        { "Objects.Rockets.LaunchMount", "lattice.support.pillars" },
        { "Assets.Scripts.Objects.Electrical.AreaPowerControl", "lattice.support.wall-mount" },
        { "Assets.Scripts.Objects.Electrical.DeviceCableMounted", "lattice.support.on-cable" },
        { "Assets.Scripts.Objects.Pipes.DevicePipeMounted", "lattice.support.on-pipe" },
        { "Objects.Pipes.OrganPipe", "lattice.support.on-pipe-valve" },
        { "Assets.Scripts.Objects.Chutes.Harvester", "lattice.support.on-tray" },
        { "Assets.Scripts.Objects.Pipes.StructureExtension", "lattice.support.on-extendable" },
        { "Objects.Rockets.RocketCrewUmbilical", "lattice.support.in-rocket-tower" },
        { "Assets.Scripts.Objects.Pipes.IndustrialFiltration", "lattice.support.outside" },
        { "Objects.Rockets.Fuselage", "lattice.support.rocket" },
        { "Assets.Scripts.Objects.Electrical.ElevatorShaft", "lattice.support.elevator-shaft" },
    };

    [Theory]
    [MemberData(nameof(Groups))]
    public void A_census_class_has_its_group(string census, string group) =>
        Assert.Equal(group, Of(Type(census, true), Type(Device, true), Type(SmallGrid, true), Type(Structure, true)).Value);

    [Theory]
    [MemberData(nameof(Groups))]
    public void A_modded_subclass_of_a_census_class_takes_its_group(string census, string group) =>
        Assert.Equal(group, Of(Type("MyMod.Machines.BetterThing"), Type(census, true), Type(Device, true), Type(Structure, true)).Value);

    [Fact]
    public void A_piece_that_needs_nothing_has_no_group() =>
        Assert.True(SupportGroups.Of(Chain(
            Type("Assets.Scripts.Objects.Electrical.Cable", true),
            Type("Assets.Scripts.Objects.SmallSingleGrid"),
            Type(SmallGrid, true),
            Type(Structure, true)), rocketOnly: false).IsNone);

    [Fact]
    public void A_modded_class_with_its_own_placement_check_asks_the_game() =>
        Assert.Equal(SupportGroups.AskGame, Of(Type("MyMod.Shelves.WallShelf", true), Type(Device, true), Type(Structure, true)));

    [Fact]
    public void A_modded_check_below_a_census_class_still_asks_the_game() =>
        Assert.Equal(
            SupportGroups.AskGame,
            Of(Type("MyMod.Machines.HoverFurnace", true), Type("Assets.Scripts.Objects.Pipes.ArcFurnace", true), Type(Structure, true)));

    [Fact]
    public void A_modded_class_without_its_own_check_takes_what_its_bases_need() =>
        Assert.True(SupportGroups.Of(Chain(Type("MyMod.Decor.Plant"), Type(Device, true), Type(Structure, true)), rocketOnly: false).IsNone);

    [Fact]
    public void The_most_derived_census_class_decides() =>
        Assert.Equal(
            SupportGroups.Outside,
            Of(Type("Assets.Scripts.Objects.Pipes.IndustrialFiltration", true), Type("Assets.Scripts.Objects.Pipes.IndustrialCombustor", true)));

    [Fact]
    public void A_strictly_internal_rocket_piece_with_no_other_group_is_rocket_only() =>
        Assert.Equal(
            SupportGroups.InRocket,
            SupportGroups.Of(Chain(Type("Assets.Scripts.Objects.Electrical.Cable", true), Type(Structure, true)), rocketOnly: true).Reduce(default(RuleId)));

    [Fact]
    public void Every_group_parses_back_from_its_text()
    {
        foreach (var group in SupportGroups.All)
            Assert.Equal(group, Ok(SupportGroups.Parse(group.Value)));
    }

    [Fact]
    public void A_mod_group_parses_as_a_mod_rule_and_junk_is_refused()
    {
        Assert.Equal("mymod/on-shelf", Ok(SupportGroups.Parse("mymod/on-shelf")).Value);
        Assert.True(SupportGroups.Parse("lattice.support.nothing-like-this").IsError);
        Assert.True(SupportGroups.Parse("").IsError);
    }

    private static RuleId Of(params TypeData[] types) => SupportGroups.Of(Chain(types), rocketOnly: false).Reduce(default(RuleId));

    private static ValueList<TypeData> Chain(params TypeData[] types) => ValueList.From(types);
}
