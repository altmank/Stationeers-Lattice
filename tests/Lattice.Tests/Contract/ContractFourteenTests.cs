using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>
/// Contract 0.14: routes keep off lines, drawn runs judged by rules, their first break reported beside the first
/// failure; each member documented as added in 0.14, and <c>along-line</c> named among a route's stop causes.
/// </summary>
public sealed class ContractFourteenTests
{
    private static string Doc(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);
        return string.Join(" ", entry.Value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void The_contract_is_zero_fourteen()
    {
        Assert.Equal(0, ApiVersion.ContractMajor);
        Assert.Equal(14, ApiVersion.ContractMinor);
    }

    [Theory]
    [InlineData("P:Lattice.Api.RouteToggles.KeepOffLines")]
    [InlineData("P:Lattice.Api.RunRequest.Rules")]
    [InlineData("P:Lattice.Api.RunReport.FirstBreak")]
    [InlineData("F:Lattice.Api.ReasonCodes.RuleBroken")]
    public void Each_new_member_says_it_was_added_in_zero_fourteen(string member) =>
        Assert.Contains("Added in contract 0.14.", Doc(member));

    [Fact]
    public void The_rule_broken_code_is_written_as_rule_broken() =>
        Assert.Equal("rule-broken", ReasonCodes.RuleBroken.Value);

    [Fact]
    public void No_route_names_along_line_among_its_stop_causes() =>
        Assert.Contains("<c>along-line</c>", XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"))
            .Descendants("member").Single(m => (string?)m.Attribute("name") == "F:Lattice.Api.ReasonCodes.NoRoute").ToString());

    [Fact]
    public void A_request_built_as_in_zero_thirteen_has_no_rules_and_keeps_to_no_lines()
    {
        var run = new RunRequest(default, new Cell(0, 0, 0), ValueList<Cell>.Empty, new Cell(5, 0, 0), Option<PlanId>.None, Lattice.Tests.Board.TestBoard.TestMaker);
        var toggles = new RouteToggles(false, false, false, false, false);
        var report = new RunReport(ValueList<Cell>.Empty, ValueList<Placement>.Empty, Option<CellFailure>.None, Empty(), Empty());

        Assert.True(run.Rules.IsNone);
        Assert.False(toggles.KeepOffLines);
        Assert.True(report.FirstBreak.IsNone);
    }

    private static Bill Empty() => new(ValueList<ItemQuantity>.Empty, ValueList<ItemQuantity>.Empty, ValueList<ItemDelta>.Empty, ValueSet<ItemKey>.Empty);
}
