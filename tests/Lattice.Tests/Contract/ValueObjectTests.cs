using System;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Value objects refuse invalid values with a result, never an exception.</summary>
public sealed class ValueObjectTests
{
    private static readonly ModId Mod = Ok(ModId.Create("mymod"));

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    public void Turn_outside_0_to_23_is_refused(int index) => Refused(Turn.Create(index));

    [Fact]
    public void Turn_23_is_accepted() => Assert.Equal(23, Ok(Turn.Create(23)).Index);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("North\nstore")]
    public void A_blank_or_multi_line_plan_name_is_refused(string name) => Refused(PlanName.Create(name));

    [Fact]
    public void A_plan_name_past_256_characters_is_refused() => Refused(PlanName.Create(new string('a', 257)));

    [Fact]
    public void A_plan_name_is_kept_as_typed() => Assert.Equal(" North store", Ok(PlanName.Create(" North store")).Value);

    [Theory]
    [InlineData("Bad Code")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("two--separators")]
    [InlineData("other/owner")]
    public void A_mod_code_that_is_not_lowercase_words_is_refused(string code) => Refused(ReasonCode.Create(Mod, code));

    [Fact]
    public void A_mod_code_carries_its_owner() => Assert.Equal("mymod/window-clear", Ok(ReasonCode.Create(Mod, "window-clear")).Value);

    [Theory]
    [InlineData("planner/ui")]
    [InlineData("Planner")]
    [InlineData("")]
    public void A_mod_id_is_bare_lowercase_words(string id) => Refused(ModId.Create(id));

    [Fact]
    public void Qualified_ids_are_written_owner_slash_name()
    {
        Assert.Equal("mymod/rocket", Ok(AttachmentKey.Create(Mod, "rocket")).Value);
        Assert.Equal("mymod/run-tool", Ok(Maker.Create(Mod, "run-tool")).Value);
        Assert.Equal("mymod/drone-3", Ok(ExecutorId.Create(Mod, "drone-3")).Value);
        Assert.Equal("mymod/lint", Ok(RuleId.Create(Mod, "lint")).Value);
    }

    [Fact]
    public void A_finish_that_asks_for_nothing_is_refused() =>
        Refused(Finish.Create(Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None));

    [Fact]
    public void Numeric_value_objects_refuse_out_of_range_input()
    {
        Refused(ItemQuantity.Create(new ItemKey(1), 0));
        Refused(KitOption.Create(-1));
        Refused(StepIndex.Create(-1));
        Refused(PaintColour.Create(-1));
        Refused(Setting.Create(double.NaN));
        Refused(Setting.Create(double.PositiveInfinity));
        Refused(Lease.Create(0));
        Refused(Lease.Create(-5));
        Refused(Lease.Create(double.PositiveInfinity));
    }

    [Fact]
    public void A_null_attachment_is_refused() => Refused(Attachment.Create(null!));

    [Fact]
    public void A_refusal_names_the_type_and_the_broken_rule()
    {
        var reason = Error(Turn.Create(30));

        Assert.Equal(ReasonCodes.InvalidValue, reason.Code);
        Assert.Equal(RuleId.Contract, reason.From);
        Assert.Equal("Turn", reason.Facts["type"]);
        Assert.Equal("30", reason.Facts["value"]);
    }

    [Fact]
    public void Defaults_are_the_documented_empty_values()
    {
        Assert.Equal("", default(PlanName).Value);
        Assert.Equal("", default(ReasonCode).Value);
        Assert.Equal(0, default(Turn).Index);
        Assert.Equal(Turn.None, default(Turn));
    }

    private static T Ok<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(v => v, r => throw new InvalidOperationException(r.Code.Value));

    private static Reason Error<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(v => throw new InvalidOperationException($"accepted {v}"), r => r);

    private static void Refused<T>(Result<T, Reason> result) where T : notnull =>
        Assert.Equal(ReasonCodes.InvalidValue, Error(result).Code);
}
