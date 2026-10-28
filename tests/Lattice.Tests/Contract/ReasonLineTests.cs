using Lattice.Api;

namespace Lattice.Tests.Contract;

public sealed class ReasonLineTests
{
    private static readonly RuleId Sync = Lattice.Core.Sync.SyncRules.Sync;

    [Fact]
    public void A_reason_without_facts_prints_its_code_and_rule()
    {
        Assert.Equal("not-found from lattice.sync", Reason.Of(ReasonCodes.NotFound, Sync).ToString());
    }

    [Fact]
    public void One_fact_prints_after_a_colon()
    {
        var why = Reason.Of(ReasonCodes.InvalidValue, Sync, ValueMap.Of("problem", "lines"));

        Assert.Equal("invalid-value from lattice.sync: problem=lines", why.ToString());
    }

    [Fact]
    public void Facts_print_sorted_by_key_whatever_order_they_were_added()
    {
        var a = Reason.Of(ReasonCodes.InvalidValue, Sync, ValueMap.Of("revision", "12").With("problem", "not-the-host-board"));
        var b = Reason.Of(ReasonCodes.InvalidValue, Sync, ValueMap.Of("problem", "not-the-host-board").With("revision", "12"));

        Assert.Equal("invalid-value from lattice.sync: problem=not-the-host-board revision=12", a.ToString());
        Assert.Equal(a.ToString(), b.ToString());
    }

    [Fact]
    public void Values_escape_what_would_break_the_line_and_keys_also_equals()
    {
        var why = Reason.Of(ReasonCodes.InvalidValue, Sync, ValueMap.Of("problem", "ghost 7 is 100% gone\n").With("a=b", "x=y"));

        Assert.Equal("invalid-value from lattice.sync: a%3Db=x=y problem=ghost%207%20is%20100%25%20gone%0A", why.ToString());
    }

    [Fact]
    public void A_game_message_prints_last_in_quotes()
    {
        var why = new Reason(ReasonCodes.GameRefused, ValueMap.Of("piece", "1"), Option.Some("No room"), Sync);

        Assert.Equal("game-refused from lattice.sync: piece=1 (game: \"No room\")", why.ToString());
    }

    [Fact]
    public void The_record_still_compares_by_value()
    {
        var a = Reason.Of(ReasonCodes.InvalidValue, Sync, ValueMap.Of("problem", "x"));

        Assert.Equal(a, Reason.Of(ReasonCodes.InvalidValue, Sync, ValueMap.Of("problem", "x")));
    }
}
