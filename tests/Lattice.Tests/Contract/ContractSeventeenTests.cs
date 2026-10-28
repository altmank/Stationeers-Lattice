using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>
/// Contract 0.17: <see cref="Reason.ToString"/> prints its code, rule and facts, and
/// <see cref="ReasonCodes.OutcomeLost"/> answers a command whose outcome a resync lost; both documented as added in 0.17.
/// </summary>
public sealed class ContractSeventeenTests
{
    private static string Doc(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);
        return string.Join(" ", entry.Value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Reason_to_string_says_it_was_added_in_zero_seventeen() =>
        Assert.Contains("Added in contract 0.17.", Doc("M:Lattice.Api.Reason.ToString"));

    [Fact]
    public void Outcome_lost_says_it_was_added_in_zero_seventeen() =>
        Assert.Contains("Added in contract 0.17.", Doc("F:Lattice.Api.ReasonCodes.OutcomeLost"));

    [Fact]
    public void Outcome_lost_is_its_own_code() =>
        Assert.Equal("outcome-lost", ReasonCodes.OutcomeLost.Value);
}
