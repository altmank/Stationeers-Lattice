using System;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Styles;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// The public catalogue view: every piece and kit by kind, sorted by prefab name, a piece's kits with the
/// game's choice first, refusals with reasons, families as style rules read them, and reads that allocate nothing.
/// </summary>
public sealed class PieceCatalogueTests
{
    private const string Text = """
        lattice-catalogue 3
        piece ModPanelB kit=ItemKitModPanels option=1 grid=large family=wall
          step material=ItemKitModPanels*1 air=yes
        piece ModPanelA kit=ItemKitModPanels option=0 grid=large family=wall
          step material=ItemKitModPanels*1 air=yes
        piece ModPanelC kit=ItemKitModPanels option=2 grid=large family=wall
          step material=ItemKitModPanels*1 air=yes
        piece ModBridge kit=ItemKitModBridge option=0 also=ItemKitModPanels:3 grid=small
          step material=ItemKitModBridge*1 air=no
        piece ModRubble grid=large
          step air=no
        """;

    private static readonly FakeCatalogue Modded = Read(Text);

    private static FakeCatalogue Read(string text) =>
        CatalogueFile.Read(new StringReader(text), "view.lcat").Match(static c => c, static e => throw new InvalidOperationException(e.ToString()));

    private static PieceCatalogue View(FakeCatalogue catalogue) => new(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue));

    private static KitOption Option(int index) => Ok(KitOption.Create(index));

    [Fact]
    public void Every_hand_kit_option_gives_its_piece_and_a_piece_whose_first_kit_it_is_gives_it_back()
    {
        var hand = FakeCatalogue.Hand();
        var view = View(hand);
        var roundTrips = 0;

        foreach (var kit in view.Kits)
            for (var i = 0; i < kit.Options.Count; i++)
            {
                var piece = Ok(view.PieceOf(kit.Key, Option(i)));
                Assert.Equal(Ok(hand.PieceOf(kit.Key, Option(i))), piece);
                Assert.Contains(new KitPlace(kit.Key, Option(i)), Ok(view.Piece(piece)).Kits);
                if (hand.Named(hand.NameOf(piece)).Kits[0] == new KitPlace(kit.Key, Option(i)))
                {
                    Assert.Equal(new KitPlace(kit.Key, Option(i)), Ok(view.KitOf(piece)));
                    roundTrips++;
                }
            }

        Assert.Equal(hand.Pieces.Count, roundTrips);
    }

    [Fact]
    public void A_kits_options_are_in_the_files_option_order()
    {
        var kit = Ok(View(Modded).Kit(PrefabHash.Kit("ItemKitModPanels")));

        Assert.Equal(new[] { "ModPanelA", "ModPanelB", "ModPanelC", "ModBridge" }, kit.Options.Select(Modded.NameOf));
    }

    [Fact]
    public void Pieces_and_kits_are_sorted_by_prefab_name()
    {
        var view = View(FakeCatalogue.Hand());

        Assert.Equal(view.Pieces.Select(p => p.PrefabName).OrderBy(n => n, StringComparer.Ordinal), view.Pieces.Select(p => p.PrefabName));
        Assert.Equal(view.Kits.Select(k => k.PrefabName).OrderBy(n => n, StringComparer.Ordinal), view.Kits.Select(k => k.PrefabName));
        Assert.Equal(FakeCatalogue.Hand().Pieces.Count, view.Pieces.Count);
    }

    [Fact]
    public void A_piece_in_two_kits_lists_both_with_the_first_kit_first()
    {
        var view = View(Modded);
        var bridge = Ok(view.Piece(PrefabHash.Piece("ModBridge")));

        Assert.Equal(
            new[] { new KitPlace(PrefabHash.Kit("ItemKitModBridge"), Option(0)), new KitPlace(PrefabHash.Kit("ItemKitModPanels"), Option(3)) },
            bridge.Kits);
        Assert.Equal(bridge.Kits[0], Ok(view.KitOf(bridge.Key)));
    }

    [Fact]
    public void The_game_catalogues_first_kit_is_the_one_the_first_build_step_takes_and_the_view_agrees_without_reading_a_prefab()
    {
        var shared = Piece("ModShared", states: new[] { State(Stack("ItemKitZed", 1), exit: "ItemWrench") });
        var source = new CountingSource(new[] { Kit("ItemKitAlpha", shared), Kit("ItemKitZed", Piece("ModOther"), shared) }, new[] { shared, Piece("ModOther") });
        var catalogue = new PrefabCatalogue(source);
        var view = new PieceCatalogue(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue));

        Assert.Equal(0, source.Reads(shared.Key));
        Assert.Equal(new KitPlace(PrefabHash.Kit("ItemKitZed"), Option(1)), Ok(view.KitOf(shared.Key)));
        Assert.Equal(Ok(catalogue.Piece(shared.Key)).Kits, Ok(view.Piece(shared.Key)).Kits);
        Assert.Equal(new[] { "ItemKitAlpha", "ItemKitZed" }, view.Kits.Select(k => k.PrefabName));
    }

    [Fact]
    public void A_kitless_piece_is_listed_with_no_kits_and_its_kit_is_not_found()
    {
        var view = View(Modded);
        var rubble = Ok(view.Piece(PrefabHash.Piece("ModRubble")));

        Assert.Empty(rubble.Kits);
        Assert.Equal(PieceGrid.Large, rubble.Grid);
        var why = Error(view.KitOf(rubble.Key));
        Assert.Equal(ReasonCodes.NotFound, why.Code);
        Assert.Equal("no-kit", why.Facts["problem"]);
    }

    [Fact]
    public void Unknown_pieces_unknown_kits_and_options_past_the_last_are_refused()
    {
        var view = View(Modded);
        var panels = PrefabHash.Kit("ItemKitModPanels");

        Assert.Equal(ReasonCodes.UnknownPiece, Error(view.Piece(PrefabHash.Piece("Nothing"))).Code);
        Assert.Equal(ReasonCodes.UnknownPiece, Error(view.KitOf(PrefabHash.Piece("Nothing"))).Code);
        Assert.Equal(ReasonCodes.UnknownPiece, Error(view.FamilyOf(PrefabHash.Piece("Nothing"))).Code);
        var noKit = Error(view.Kit(PrefabHash.Kit("ItemNothing")));
        Assert.Equal(ReasonCodes.UnknownPiece, noKit.Code);
        Assert.True(noKit.Facts.ContainsKey("kit"));
        Assert.Equal(ReasonCodes.UnknownPiece, Error(view.PieceOf(PrefabHash.Kit("ItemNothing"), Option(0))).Code);
        Assert.Equal(ReasonCodes.InvalidValue, Error(view.PieceOf(panels, Option(4))).Code);
    }

    [Fact]
    public void A_family_comes_from_the_type_until_a_mod_registers_one_and_back_after_dispose_as_styles_read_it()
    {
        var additions = new CatalogueAdditions(Modded);
        var view = new PieceCatalogue(Modded.Listing, additions, PiecePorts.Over(Modded), Lattice.Core.Styles.Facings.Over(Modded));
        var looks = new CataloguePieceLooks(Modded, additions);
        var panel = PrefabHash.Piece("ModPanelA");
        var trim = Ok(PieceFamily.Create(Ok(ModId.Create("planner")), "trim"));

        Assert.Equal(PieceFamilies.Wall, Ok(view.FamilyOf(panel)));
        var handle = Ok(additions.Register(new CatalogueAddition.InFamily(panel, trim)));
        Assert.Equal(trim, Ok(view.FamilyOf(panel)));
        Assert.Equal(looks.Family(panel), Ok(view.FamilyOf(panel)));
        handle.Dispose();
        Assert.Equal(PieceFamilies.Wall, Ok(view.FamilyOf(panel)));
        Assert.Equal(looks.Family(panel), Ok(view.FamilyOf(panel)));
    }

    [Fact]
    public void Repeated_reads_allocate_nothing()
    {
        var view = View(Modded);
        var panels = PrefabHash.Kit("ItemKitModPanels");
        var panel = PrefabHash.Piece("ModPanelA");
        var rubble = PrefabHash.Piece("ModRubble");
        var option = Option(2);
        view.FamilyOf(panel);

        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => view.Piece(panel), "Piece");
        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => view.Kit(panels), "Kit");
        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => view.PieceOf(panels, option), "PieceOf");
        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => view.KitOf(panel), "KitOf");
        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => view.KitOf(rubble), "KitOf of a kitless piece");
        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => view.FamilyOf(panel), "FamilyOf");
        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => _ = view.Pieces[view.Pieces.Count - 1].Kits.Count + view.Kits[0].Options.Count, "lists");
    }

    [Fact]
    public void The_summary_leaves_names_out_and_changes_with_the_option_order()
    {
        var listing = Modded.Listing;
        var renamed = listing with { Pieces = ValueList.From(listing.Pieces.Select(p => p with { Name = "Anders " + p.Name })) };
        var swapped = listing with
        {
            Kits = ValueList.From(listing.Kits.Select(k => k.Options.Count < 2 ? k : k with { Options = ValueList.From(k.Options.Reverse()) })),
        };

        var summary = CatalogueSummary.Of(new PieceCatalogue(listing, new CatalogueAdditions(Modded), PiecePorts.Over(Modded), Lattice.Core.Styles.Facings.Over(Modded)));

        Assert.Equal(summary, CatalogueSummary.Of(new PieceCatalogue(renamed, new CatalogueAdditions(Modded), PiecePorts.Over(Modded), Lattice.Core.Styles.Facings.Over(Modded))));
        Assert.NotEqual(summary, CatalogueSummary.Of(new PieceCatalogue(swapped, new CatalogueAdditions(Modded), PiecePorts.Over(Modded), Lattice.Core.Styles.Facings.Over(Modded))));
        Assert.StartsWith("pieces=5 kits=2 hash=", summary);
    }
}
