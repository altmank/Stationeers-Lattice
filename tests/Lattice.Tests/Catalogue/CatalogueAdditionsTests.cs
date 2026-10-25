using System;
using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.TestKit.Catalogue;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// The per-world registry of facts the bridge cannot read for a modded piece (architecture 6.8, "Catalogue additions"):
/// a mirror partner, held both ways, ended by its handle; refused for a piece the catalogue lacks, a piece named as its
/// own mirror, a pair on different grids, and a piece that already has a partner.
/// </summary>
public sealed class CatalogueAdditionsTests
{
    private const string Text = """
        lattice-catalogue 3
        piece ModTriangleLeft kit=ItemKitModTriangle option=0 grid=large
          step material=ItemKitModTriangle*1 air=no
        piece ModTriangleRight kit=ItemKitModTriangle option=1 grid=large
          step material=ItemKitModTriangle*1 air=no
        piece ModCornerLeft kit=ItemKitModCorner option=0 grid=large
          step material=ItemKitModCorner*1 air=no
        piece ModBracket kit=ItemKitModBracket option=0 grid=small
          step material=ItemKitModBracket*1 air=no
        piece StructureWallLike kit=ItemKitWallLike option=0 grid=large family=wall
          step material=ItemKitWallLike*1 air=yes
        """;

    private static readonly FakeCatalogue Catalogue =
        CatalogueFile.Read(new StringReader(Text), "additions.lcat").Match(static c => c, static e => throw new InvalidOperationException(e.ToString()));

    private static PrefabKey Key(string name) => Catalogue.Named(name).Key;

    private static CatalogueAddition Pair(string piece, string mirror) => new CatalogueAddition.MirrorPartner(Key(piece), Key(mirror));

    [Fact]
    public void A_mirror_partner_is_read_both_ways()
    {
        var additions = new CatalogueAdditions(Catalogue);

        Assert.True(additions.Register(Pair("ModTriangleLeft", "ModTriangleRight")).IsOk);

        Assert.Equal(Option.Some(Key("ModTriangleRight")), additions.PartnerOf(Key("ModTriangleLeft")));
        Assert.Equal(Option.Some(Key("ModTriangleLeft")), additions.PartnerOf(Key("ModTriangleRight")));
        Assert.True(additions.PartnerOf(Key("ModCornerLeft")).IsNone);
    }

    [Fact]
    public void Disposing_the_handle_ends_the_partnership_and_frees_both_pieces()
    {
        var additions = new CatalogueAdditions(Catalogue);
        var handle = additions.Register(Pair("ModTriangleLeft", "ModTriangleRight")).Match(static h => h, static e => throw new InvalidOperationException(e.ToString()));

        handle.Dispose();
        handle.Dispose();

        Assert.True(additions.PartnerOf(Key("ModTriangleLeft")).IsNone);
        Assert.True(additions.PartnerOf(Key("ModTriangleRight")).IsNone);
        Assert.True(additions.Register(Pair("ModTriangleRight", "ModCornerLeft")).IsOk);
    }

    [Fact]
    public void A_piece_that_already_has_a_partner_is_refused_and_the_first_pair_stands()
    {
        var additions = new CatalogueAdditions(Catalogue);
        additions.Register(Pair("ModTriangleLeft", "ModTriangleRight"));

        var refused = additions.Register(Pair("ModCornerLeft", "ModTriangleRight"));

        Assert.Equal(ReasonCodes.AlreadyRegistered, Code(refused));
        Assert.Equal(Option.Some(Key("ModTriangleLeft")), additions.PartnerOf(Key("ModTriangleRight")));
        Assert.True(additions.PartnerOf(Key("ModCornerLeft")).IsNone);
    }

    [Fact]
    public void A_piece_the_catalogue_lacks_is_refused_as_unknown()
    {
        var additions = new CatalogueAdditions(Catalogue);

        var refused = additions.Register(new CatalogueAddition.MirrorPartner(Key("ModTriangleLeft"), PrefabHash.Piece("ModNotLoaded")));

        Assert.Equal(ReasonCodes.UnknownPiece, Code(refused));
        Assert.True(additions.PartnerOf(Key("ModTriangleLeft")).IsNone);
    }

    [Fact]
    public void A_piece_as_its_own_mirror_and_a_pair_on_different_grids_are_invalid()
    {
        var additions = new CatalogueAdditions(Catalogue);

        Assert.Equal(ReasonCodes.InvalidValue, Code(additions.Register(Pair("ModTriangleLeft", "ModTriangleLeft"))));
        Assert.Equal(ReasonCodes.InvalidValue, Code(additions.Register(Pair("ModTriangleLeft", "ModBracket"))));
        Assert.True(additions.PartnerOf(Key("ModTriangleLeft")).IsNone);
    }

    [Fact]
    public void A_modded_piece_joins_a_family_and_leaves_it_when_the_handle_is_disposed()
    {
        var additions = new CatalogueAdditions(Catalogue);
        Assert.Equal(PieceFamilies.Other, additions.FamilyOf(Key("ModBracket")));

        var handle = Ok(additions.Register(new CatalogueAddition.InFamily(Key("ModBracket"), PieceFamilies.Device)));

        Assert.Equal(PieceFamilies.Device, additions.FamilyOf(Key("ModBracket")));
        handle.Dispose();
        handle.Dispose();
        Assert.Equal(PieceFamilies.Other, additions.FamilyOf(Key("ModBracket")));
    }

    [Fact]
    public void A_mods_own_family_comes_before_the_one_found_by_type()
    {
        var additions = new CatalogueAdditions(Catalogue);
        var hulls = Ok(PieceFamily.Create(Ok(ModId.Create("hexmod")), "hull-plates"));

        Ok(additions.Register(new CatalogueAddition.InFamily(Key("StructureWallLike"), hulls)));

        Assert.Equal(hulls, additions.FamilyOf(Key("StructureWallLike")));
        Assert.Equal(PieceFamilies.Wall, Catalogue.Named("StructureWallLike").Family);
    }

    [Fact]
    public void A_family_for_an_unknown_piece_a_default_family_and_a_second_family_are_refused()
    {
        var additions = new CatalogueAdditions(Catalogue);
        Ok(additions.Register(new CatalogueAddition.InFamily(Key("ModBracket"), PieceFamilies.Device)));

        Assert.Equal(ReasonCodes.UnknownPiece, Code(additions.Register(new CatalogueAddition.InFamily(PrefabHash.Piece("ModNotLoaded"), PieceFamilies.Wall))));
        Assert.Equal(ReasonCodes.InvalidValue, Code(additions.Register(new CatalogueAddition.InFamily(Key("ModCornerLeft"), default))));
        Assert.Equal(ReasonCodes.AlreadyRegistered, Code(additions.Register(new CatalogueAddition.InFamily(Key("ModBracket"), PieceFamilies.Cable))));
        Assert.Equal(PieceFamilies.Device, additions.FamilyOf(Key("ModBracket")));
        Assert.Equal(PieceFamilies.Other, additions.FamilyOf(Key("ModCornerLeft")));
    }

    [Fact]
    public void A_piece_the_catalogue_lacks_is_other()
    {
        Assert.Equal(PieceFamilies.Other, new CatalogueAdditions(Catalogue).FamilyOf(PrefabHash.Piece("ModNotLoaded")));
    }

    private static T Ok<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static v => v, static e => throw new InvalidOperationException(e.ToString()));

    private static ReasonCode Code(Result<IDisposable, Reason> result) =>
        result.TryGetError(out var why) ? why.Code : throw new InvalidOperationException("Expected a refusal.");
}
