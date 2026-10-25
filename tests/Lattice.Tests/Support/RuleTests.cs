using System;
using Lattice.Tests.Slices;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Support;
using Lattice.TestKit;
using static Lattice.Tests.Support.SupportBench;

namespace Lattice.Tests.Support;

/// <summary>
/// Mods' support rules (Adds and Replaces), the decided rule treatment (time limit, switched off if they throw) and the
/// game-check fallback for pieces Lattice cannot classify.
/// </summary>
public sealed class RuleTests
{
    private static readonly Cell OnTopOfOrigin = new(10, 20, 10);
    private static readonly ModId Mod = TestKitIds.Must(ModId.Create("othermod"));

    private static RuleId Id(string name) => TestKitIds.Must(RuleId.Create(Mod, name));

    private static SupportRuleDescriptor Rule(string name, string piece, Func<SupportInput, SupportVerdict> check)
    {
        var key = Pieces.Named(piece).Key;
        return new SupportRuleDescriptor(Id(name), p => p == key, check);
    }

    private static SupportVerdict Holds() => new SupportVerdict.SupportedBy(ValueList<StructureRef>.Empty);

    private static SupportVerdict Refuses(string code) =>
        new SupportVerdict.Unsupported(Reason.Of(TestKitIds.Must(ReasonCode.Create(Mod, code)), Id("rule")));

    [Fact]
    public void An_unknown_piece_asks_the_game_and_is_pending_until_it_answers()
    {
        var built = Scenario().Ghost("ModOddity", new Cell(5, 5, 5), out var odd).Build();
        var at = PlacementOf(built, odd);

        Assert.Equal([at], Assert.IsType<OrderOutcome.Pending>(Order(built)).Ask);
        Assert.IsType<Finding.Passes>(Verdict(Ready(Order(built, Answers((at, new GameAnswer.Accepts())))), odd));
    }

    [Fact]
    public void An_unknown_piece_the_game_refuses_fails_with_the_games_message()
    {
        var built = Scenario().Ghost("ModOddity", new Cell(5, 5, 5), out var odd).Build();
        var at = PlacementOf(built, odd);

        var report = Ready(Order(built, Answers((at, new GameAnswer.Refuses(Option.Some("Needs a widget"))))));

        var fails = Assert.IsType<Finding.WillFail>(Verdict(report, odd));
        Assert.Equal(ReasonCodes.GameRefused, fails.Why.Code);
        Assert.Equal(SupportGroups.AskGame, fails.Why.From);
        Assert.Equal(Option.Some("Needs a widget"), fails.Why.GameMessage);
    }

    [Fact]
    public void An_unknown_piece_next_to_ghosts_waits_for_them_whatever_the_game_says_now()
    {
        var built = Scenario()
            .Ghost("ModOddity", new Cell(5, 5, 5), out var odd)
            .Ghost("StructureCableStraight", new Cell(5, 5, 10), out var cable)
            .Built("StructureFrameIron", Large(0, 0, 0), out var frame)
            .Removal(frame, out var removal)
            .Build();
        var at = PlacementOf(built, odd);

        var report = Ready(Order(built, Answers((at, new GameAnswer.Refuses(Option<string>.None)))));

        AssertWaitsFor(Verdict(report, odd), cable, removal);
    }

    [Fact]
    public void A_piece_in_a_group_lattice_does_not_model_asks_the_game()
    {
        var built = Scenario().Ghost("ModRocketPart", Large(0, 0, 0), out _).Build();

        Assert.IsType<OrderOutcome.Pending>(Order(built));
    }

    [Fact]
    public void A_replacing_rule_answers_alone_for_a_piece_the_game_would_answer()
    {
        var rules = Rules();
        rules.Register(Rule("rocket", "ModRocketPart", _ => Holds()) with { Mode = SupportMode.Replaces });
        var built = Scenario().Ghost("ModRocketPart", Large(0, 0, 0), out var part).Build();

        Assert.IsType<Finding.Passes>(Verdict(Ready(Order(built, rules: rules)), part));
    }

    [Fact]
    public void A_replacing_rule_answers_alone_for_a_piece_lattice_models()
    {
        var rules = Rules();
        rules.Register(Rule("hover", "ModFloorMachine", _ => Holds()) with { Mode = SupportMode.Replaces });
        var built = Scenario().Ghost("ModFloorMachine", OnTopOfOrigin, out var machine).Build();

        Assert.IsType<Finding.Passes>(Verdict(Ready(Order(built, rules: rules)), machine));
    }

    [Fact]
    public void An_adding_rule_is_checked_as_well_as_the_census_group()
    {
        var rules = Rules();
        rules.Register(Rule("strict", "ModFloorMachine", _ => Refuses("too-heavy")));
        var withFrame = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0))
            .Ghost("ModFloorMachine", OnTopOfOrigin, out var onFrame)
            .Build();
        var loose = Rules();
        loose.Register(Rule("loose", "ModFloorMachine", _ => Holds()));
        var noFrame = Scenario().Ghost("ModFloorMachine", OnTopOfOrigin, out var floating).Build();

        var strict = Assert.IsType<Finding.WillFail>(Verdict(Ready(Order(withFrame, rules: rules)), onFrame));
        Assert.Equal("othermod/too-heavy", strict.Why.Code.Value);
        AssertNeedsSupport(Verdict(Ready(Order(noFrame, rules: loose)), floating), SupportGroups.FrameBelow, "PlacementRequiresFrame");
    }

    [Fact]
    public void A_mods_own_group_is_the_games_to_answer_until_its_rule_is_registered()
    {
        var built = Scenario().Ghost("ModGadget", new Cell(5, 5, 5), out var gadget).Build();
        var rules = Rules();
        rules.Register(Rule("gadget", "ModGadget", _ => Holds()) with { Mode = SupportMode.Replaces });

        Assert.IsType<OrderOutcome.Pending>(Order(built));
        Assert.IsType<Finding.Passes>(Verdict(Ready(Order(built, rules: rules)), gadget));
    }

    [Fact]
    public void Supports_a_mods_rule_names_that_are_only_planned_make_the_ghost_wait()
    {
        var rules = Rules();
        rules.Register(
            new SupportRuleDescriptor(Id("on-frame"), p => p == Pieces.Named("ModGadget").Key, input =>
            {
                var below = input.World.At(Large(0, 0, 0));
                return below.Count > 0
                    ? new SupportVerdict.SupportedBy(ValueList.Of(below[0].Ref))
                    : Refuses("no-frame");
            }) with { Mode = SupportMode.Replaces });
        var built = Scenario()
            .Ghost("StructureFrameIron", Large(0, 0, 0), out var frame)
            .Ghost("ModGadget", new Cell(10, 20, 10), out var gadget)
            .Build();

        var report = Ready(Order(built, rules: rules));

        AssertWaitsFor(Verdict(report, gadget), frame);
        AssertStages(report, [frame], [gadget]);
    }

    [Fact]
    public void Several_replacing_rules_must_all_hold()
    {
        var rules = Rules();
        rules.Register(Rule("a", "ModRocketPart", _ => Holds()) with { Mode = SupportMode.Replaces });
        rules.Register(Rule("b", "ModRocketPart", _ => Refuses("b-says-no")) with { Mode = SupportMode.Replaces });
        var built = Scenario().Ghost("ModRocketPart", Large(0, 0, 0), out var part).Build();

        var fails = Assert.IsType<Finding.WillFail>(Verdict(Ready(Order(built, rules: rules)), part));
        Assert.Equal("othermod/b-says-no", fails.Why.Code.Value);
    }

    [Fact]
    public void A_second_rule_with_one_id_is_refused_and_disposing_ends_a_registration()
    {
        var rules = Rules();
        var first = rules.Register(Rule("x", "ModRocketPart", _ => Holds()) with { Mode = SupportMode.Replaces });
        var again = rules.Register(Rule("x", "ModFuse", _ => Holds()));

        Assert.True(again.TryGetError(out var refused));
        Assert.Equal(ReasonCodes.AlreadyRegistered, refused.Code);
        Assert.True(first.TryGet(out var registration));
        registration.Dispose();
        Assert.Empty(rules.Current);
        Assert.True(rules.Register(Rule("x", "ModFuse", _ => Holds())).IsOk);
    }

    [Fact]
    public void A_throwing_rule_is_switched_off_reported_once_and_the_piece_judged_without_it()
    {
        var rules = Rules();
        rules.Register(Rule("boom", "ModFloorMachine", _ => throw new InvalidOperationException("mod bug")) with { Mode = SupportMode.Replaces });
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0))
            .Ghost("ModFloorMachine", OnTopOfOrigin, out var machine)
            .Build();

        var first = Ready(Order(built, rules: rules));
        var second = Ready(Order(built, rules: rules));

        Assert.IsType<Finding.Passes>(Verdict(first, machine));
        Assert.Contains(Warnings(first, machine), w => w.Code == ReasonCodes.RuleSwitchedOff && w.From == Id("boom"));
        Assert.IsType<Finding.Passes>(Verdict(second, machine));
        Assert.DoesNotContain(Warnings(second, machine), w => w.Code == ReasonCodes.RuleSwitchedOff);
    }

    [Fact]
    public void A_rule_past_the_time_limit_is_dropped_with_a_warning()
    {
        var clock = new FakeClock();
        var rules = Rules(clock);
        rules.Register(Rule("slow", "ModFloorMachine", _ =>
        {
            clock.Advance(SupportRules.DefaultLimit + TimeSpan.FromMilliseconds(1));
            return Refuses("slow-no");
        }));
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0))
            .Ghost("ModFloorMachine", OnTopOfOrigin, out var machine)
            .Build();

        var report = Ready(Order(built, rules: rules));

        Assert.IsType<Finding.Passes>(Verdict(report, machine));
        Assert.Contains(Warnings(report, machine), w => w.Code == ReasonCodes.RuleTimedOut && w.From == Id("slow"));
    }

    [Fact]
    public void A_modded_subclass_of_a_census_class_takes_its_group()
    {
        var types = ValueList.From(new[]
        {
            new TypeData("OtherMod.Objects.HyperFurnace", false),
            new TypeData("Assets.Scripts.Objects.Pipes.ArcFurnace", true),
            new TypeData("Assets.Scripts.Objects.Structure", true),
        });

        Assert.Equal(Option.Some(SupportGroups.FrameBelow), SupportGroups.Of(types, rocketOnly: false));
    }
}
