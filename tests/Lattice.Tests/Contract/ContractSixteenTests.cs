using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>
/// Contract 0.16: <see cref="Lattice.Api.Preview.Runs"/> carries one run report per run or route step, documented as
/// added in 0.16, and <see cref="Lattice.Api.Preview.Run"/> names a batch's first.
/// </summary>
public sealed class ContractSixteenTests
{
    private static string Doc(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);
        return string.Join(" ", entry.Value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Runs_says_it_was_added_in_zero_sixteen() =>
        Assert.Contains("Added in contract 0.16.", Doc("P:Lattice.Api.Preview.Runs"));

    [Fact]
    public void Run_says_what_it_holds_for_a_batch() =>
        Assert.Contains("For a batch: the report of its first run or route step", Doc("P:Lattice.Api.Preview.Run"));
}
