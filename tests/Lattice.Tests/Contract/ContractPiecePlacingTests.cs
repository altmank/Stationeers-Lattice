using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Lattice 0.22.1: each piece's placement, documented as added in 0.22.1, inside contract 0.22.</summary>
public sealed class ContractPiecePlacingTests
{
    private const string Added = "Added in 0.22.1.";

    private static string Doc(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);
        return string.Join(" ", entry.Value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("P:Lattice.Api.PieceEntry.Placing")]
    [InlineData("T:Lattice.Api.PiecePlacing")]
    public void Each_added_member_says_when_it_was_added(string member) => Assert.Contains(Added, Doc(member));

    [Fact]
    public void The_placing_comes_inside_contract_zero_twenty_two()
    {
        Assert.Equal(0, ApiVersion.ContractMajor);
        Assert.Equal(22, ApiVersion.ContractMinor);
    }

    [Fact]
    public void The_shared_cases_equal_new_ones_and_differ_from_each_other()
    {
        Assert.Equal(new PiecePlacing.Grid(), PiecePlacing.InGrid);
        Assert.Equal(new PiecePlacing.Face(), PiecePlacing.OnFace);
        Assert.Equal(new PiecePlacing.FaceMount(), PiecePlacing.OnFaceMount);
        Assert.NotEqual(PiecePlacing.OnFace, PiecePlacing.OnFaceMount);
        Assert.NotEqual(
            new PieceEntry(new PrefabKey(1), "Piece", "Piece", PieceGrid.Large, ValueList<KitPlace>.Empty),
            new PieceEntry(new PrefabKey(1), "Piece", "Piece", PieceGrid.Large, ValueList<KitPlace>.Empty) { Placing = PiecePlacing.OnFace });
    }
}
