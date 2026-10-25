using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Modules;
using Lattice.Core.Ports;
using Lattice.Core.Support;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.TestKit;
using Xunit;

namespace Lattice.Tests.Contract;

/// <summary>Support modes, support facts from the catalogue, build-state signals and game members with parameter lists.</summary>
public sealed class PortChangeTests
{
    [Fact]
    public void A_support_rule_adds_unless_it_declares_it_replaces()
    {
        var rule = new SupportRuleDescriptor(
            TestKitIds.Must(RuleId.Create(TestKitIds.Must(ModId.Create("othermod")), "mount")),
            _ => true,
            _ => new SupportVerdict.SupportedBy(ValueList<StructureRef>.Empty));

        Assert.Equal(SupportMode.Adds, rule.Mode);
        var rules = new SupportRules();
        rules.Register(rule with { Mode = SupportMode.Replaces });
        Assert.Equal(SupportMode.Replaces, rules.Current[0].Mode);
    }

    [Fact]
    public void Support_facts_come_from_the_catalogue_and_are_none_where_it_did_not_read_them()
    {
        var hand = FakeCatalogue.Hand();
        var frame = hand.Pieces.Single(p => p.Name == "StructureFrameIron");
        var wall = hand.Pieces.Single(p => p.Name == "StructureWallIron");
        var frameFacts = new SupportFacts(true, true, false, ValueSet<HostRole>.Empty);
        var catalogue = new FakeCatalogue(new List<NamedPiece>
        {
            frame with { Facts = frame.Facts with { Support = Option.Some(frameFacts) } },
            wall,
        });

        var facts = new CatalogueSupportFacts(catalogue);

        Assert.Equal(Option.Some(frameFacts), facts.Of(frame.Facts.Key));
        Assert.Equal(Option<SupportFacts>.None, facts.Of(wall.Facts.Key));
        Assert.Equal(Option<SupportFacts>.None, facts.Of(new PrefabKey(123456)));
    }

    [Fact]
    public void A_broken_structure_is_signalled_with_its_state()
    {
        var world = Worlds.Mixed().World;
        var heard = new List<(StructureRef, BuildState)>();
        world.StateChanged += (structure, state) => heard.Add((structure, state));

        world.ChangeState(new ReferenceId(1), new BuildState.Broken());

        var (signalled, state) = Assert.Single(heard);
        Assert.Equal(new ReferenceId(1), signalled.Id);
        Assert.Equal(new BuildState.Broken(), state);
        Assert.True(world.Structure(new ReferenceId(1)).TryGet(out var info));
        Assert.Equal(new BuildState.Broken(), info.State);
    }

    [Fact]
    public void Methods_with_the_same_name_differ_by_their_parameter_types()
    {
        var byInt = new GameMember.Method("Assets.Scripts.Objects.Thing", "SetCustomColor", ValueList.From(new[] { "System.Int32", "System.Boolean" }));
        var byBool = new GameMember.Method("Assets.Scripts.Objects.Thing", "SetCustomColor", ValueList.Of("System.Boolean"));

        Assert.NotEqual<GameMember>(byInt, byBool);
        Assert.Equal<GameMember>(byInt, byInt with { Parameters = ValueList.From(new[] { "System.Int32", "System.Boolean" }) });
        Assert.Equal("Assets.Scripts.Objects.Thing", byInt.TypeName);
        Assert.Equal("SetCustomColor", byInt.MemberName);
    }

    [Fact]
    public void The_codes_consumers_receive_from_support_consequences_and_runs_are_in_the_contract()
    {
        var codes = typeof(ReasonCodes).GetFields().Select(f => ((ReasonCode)f.GetValue(null)!).Value).ToHashSet();

        Assert.Subset(codes, new HashSet<string>
        {
            "cycle", "no-removable-chain", "would-bridge", "would-split", "contents-would-move",
            "would-overload-when-on", "room-gone", "room-cells-changed", "room-air-changed", "no-piece",
        });
    }
}
