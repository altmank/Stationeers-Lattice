using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Lattice 0.22.2: a face piece's snap, documented as added in 0.22.2, inside contract 0.22.</summary>
public sealed class ContractFaceSnapTests
{
    private const string Added = "in 0.22.2.";

    private static string Doc(string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(ApiVersion).Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);
        return string.Join(" ", entry.Value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("P:Lattice.Api.PieceSnap.OnFaces")]
    [InlineData("M:Lattice.Api.AxisSnap.OnLine(System.Int32)")]
    [InlineData("M:Lattice.Api.PieceSnap.Holds(Lattice.Api.Cell)")]
    [InlineData("M:Lattice.Api.PieceSnap.OffAxis(Lattice.Api.Cell)")]
    public void Each_added_or_changed_member_says_since_when(string member) => Assert.Contains(Added, Doc(member));

    [Fact]
    public void The_face_snap_comes_inside_contract_zero_twenty_two()
    {
        Assert.Equal(0, ApiVersion.ContractMajor);
        Assert.Equal(22, ApiVersion.ContractMinor);
    }

    [Fact]
    public void A_snap_built_without_on_faces_is_not_one_and_the_flag_counts_in_equality()
    {
        Assert.False(PieceSnap.Of(PieceGrid.Large).OnFaces);
        Assert.NotEqual(PieceSnap.Of(PieceGrid.Large), PieceSnap.Of(PieceGrid.Large) with { OnFaces = true });
    }
}
