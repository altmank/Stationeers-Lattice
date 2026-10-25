using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.EntryPoint;
using Lattice.Core.Executors;
using Lattice.Core.Modules;
using Lattice.Core.World;

namespace Lattice.Tests.EntryPoint;

/// <summary>Game members the running game lacks, named by the member; everything else is there.</summary>
internal sealed class FakeMembers(params GameMember[] missing) : IGameMembers
{
    public ValueList<GameMember> Missing(ModuleManifest manifest) =>
        ValueList.From(manifest.Uses.Where(missing.Contains).ToList());
}

public sealed class ModuleSwitchTests
{
    private static ModuleTable Decide(WorldSeat seat = WorldSeat.Host, params GameMember[] missing) =>
        ModuleSwitches.Decide(LatticeModules.All, new FakeMembers(missing), seat);

    private static ModuleName Name(string value) => LatticeModules.Name(value);

    private static Reason Why(ModuleTable table, string module) =>
        table.Why(Name(module)).TryGet(out var why) ? why : throw new Xunit.Sdk.XunitException(module + " is on");

    [Fact]
    public void With_every_member_there_every_module_runs_on_the_host()
    {
        var table = Decide();

        Assert.All(table.Statuses, status => Assert.IsType<ModuleState.On>(status.State));
    }

    [Fact]
    public void A_missing_world_member_turns_off_the_world_and_only_what_needs_it()
    {
        var table = Decide(WorldSeat.Host, WorldManifest.SetCustomColor);

        var off = table.Statuses.Where(s => s.State is ModuleState.Off).Select(s => s.Name.Value).ToHashSet();
        Assert.Equal(
            new HashSet<string> { "world", "mirror", "matching", "selectors", "validation", "steps", "safety", "plans", "runs", "routes", "swaps", "finished-plans", "executors", "preview", "analysis" },
            off);
        Assert.True(table.IsOn(LatticeModules.Saving));
        Assert.True(table.IsOn(LatticeModules.Transforms));
        Assert.True(table.IsOn(LatticeModules.Sync));
        Assert.True(table.IsOn(LatticeModules.Catalogue));
    }

    [Fact]
    public void The_module_with_the_missing_member_names_it_and_its_dependants_name_the_module()
    {
        var table = Decide(WorldSeat.Host, WorldManifest.SetCustomColor);

        var world = Why(table, "world");
        Assert.Equal(ReasonCodes.ModuleMissing, world.Code);
        Assert.Equal(EntryCodes.Rule, world.From);
        Assert.Equal("world", world.Facts["module"]);
        Assert.Equal("Assets.Scripts.Objects.Thing.SetCustomColor", world.Facts["missing"]);
        Assert.Equal("1", world.Facts["missing-count"]);

        var mirror = Why(table, "mirror");
        Assert.Equal("mirror", mirror.Facts["module"]);
        Assert.Equal("world", mirror.Facts["needs"]);
        Assert.Equal("mirror", Why(table, "validation").Facts["needs"]);
        Assert.Equal("runs", Why(table, "routes").Facts["needs"]);
    }

    [Fact]
    public void An_off_reason_names_at_most_eight_members_and_counts_them_all()
    {
        var missing = WorldManifest.Module.Uses.Take(11).ToArray();

        var world = Why(Decide(WorldSeat.Host, missing), "world");

        Assert.Equal(8, world.Facts["missing"].Split(',').Length);
        Assert.Equal("11", world.Facts["missing-count"]);
    }

    [Fact]
    public void A_missing_saving_member_turns_off_saving_and_what_reads_plan_files()
    {
        var table = Decide(WorldSeat.Host, Lattice.Core.Saving.SavingManifest.Module.Uses[0]);

        Assert.False(table.IsOn(LatticeModules.Saving));
        Assert.False(table.IsOn(LatticeModules.Inspector));
        Assert.False(table.IsOn(LatticeModules.FinishedPlans));
        Assert.True(table.IsOn(LatticeModules.Validation));
        Assert.True(table.IsOn(LatticeModules.World));
    }

    [Fact]
    public void A_client_runs_the_mirror_validation_and_previews_but_not_the_hosts_modules()
    {
        var table = Decide(WorldSeat.Client);

        foreach (var module in new[] { "world", "mirror", "selectors", "validation", "steps", "safety", "plans", "runs", "routes", "swaps", "transforms", "templates", "executors", "sync", "preview", "analysis" })
            Assert.True(table.IsOn(Name(module)), module);
        foreach (var module in new[] { "matching", "saving", "inspector", "finished-plans" })
            Assert.False(table.IsOn(Name(module)), module);
        Assert.Equal("client", Why(table, "matching").Facts["seat"]);
        Assert.Equal("client", Why(table, "saving").Facts["seat"]);
    }

    [Fact]
    public void A_module_that_needs_one_named_later_or_nowhere_is_off_and_says_so()
    {
        var a = LatticeModules.Name("mod-a");
        var b = LatticeModules.Name("mod-b");
        var cycle = ValueList.From(new[]
        {
            new ModuleEntry(new ModuleManifest(a, ValueList.Of(b), ValueList<GameMember>.Empty), ModuleSeats.Both),
            new ModuleEntry(new ModuleManifest(b, ValueList.Of(a), ValueList<GameMember>.Empty), ModuleSeats.Both),
        });

        var table = ModuleSwitches.Decide(cycle, new FakeMembers(), WorldSeat.Host);

        Assert.Equal("not-before", Why(table, "mod-a").Facts["problem"]);
        Assert.Equal("mod-a", Why(table, "mod-b").Facts["needs"]);
    }

    [Fact]
    public void A_module_the_table_does_not_name_is_off()
    {
        var table = Decide();

        Assert.False(table.IsOn(Name("nothing-like-it")));
        Assert.Equal(ReasonCodes.ModuleMissing, table.Missing(Name("nothing-like-it")).Code);
    }

    [Fact]
    public void Modules_come_in_dependency_order_with_unique_names()
    {
        var seen = new HashSet<ModuleName>();
        foreach (var module in LatticeModules.All)
        {
            Assert.All(module.Manifest.Needs, need => Assert.Contains(need, seen));
            Assert.True(seen.Add(module.Name), module.Name.Value);
        }
    }
}
