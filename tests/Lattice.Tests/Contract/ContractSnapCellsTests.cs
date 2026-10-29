using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Contract 0.18: each piece's snap and the cells a piece fills, documented as added in 0.18.</summary>
public sealed class ContractSnapCellsTests
{
    private const string Added = "Added in contract 0.18.";

    private static string Doc(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);
        return string.Join(" ", entry.Value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("P:Lattice.Api.PieceEntry.Snap")]
    [InlineData("T:Lattice.Api.PieceSnap")]
    [InlineData("T:Lattice.Api.AxisSnap")]
    [InlineData("M:Lattice.Api.IPieceCatalogue.CellsAt(Lattice.Api.Placement)")]
    [InlineData("F:Lattice.Api.ReasonCodes.OffSnap")]
    public void Each_added_member_says_when_it_was_added(string member) => Assert.Contains(Added, Doc(member));
}
