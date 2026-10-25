using System.Linq;
using Lattice.Api;
using static Lattice.Testing.Tests.Hosted;

namespace Lattice.Testing.Tests;

/// <summary>
/// The catalogue view as a consumer's tests reach it: the hand catalogue's, and a world's with the scenario's
/// modded kits, options, second kits and display names in it.
/// </summary>
public sealed class CatalogueViewTests
{
    private static TestPiece Panel(string name) =>
        Must(TestPiece.Create(name, "ItemKitModPanels", PieceGrid.Large)).WithCell(new CellOffset(0, 0, 0), TestSlot.FaceZPlus);

    [Fact]
    public void A_modded_kit_with_three_options_lists_them_in_option_order_and_gives_each_back()
    {
        var scenario = TestScenario.Empty
            .WithPiece(Panel("ModPanelTall").AtOption(2))
            .WithPiece(Panel("ModPanelFlat").AtOption(0))
            .WithPiece(Panel("ModPanelWide").AtOption(1));
        using var world = Start(scenario);
        var catalogue = world.System.Catalogue;
        var kit = Must(catalogue.Kit(PrefabKitKey("ItemKitModPanels")));

        Assert.Equal(new[] { "ModPanelFlat", "ModPanelWide", "ModPanelTall" }, kit.Options.Select(o => Must(catalogue.Piece(o)).PrefabName));
        for (var i = 0; i < 3; i++)
        {
            var option = Must(KitOption.Create(i));
            var piece = Must(catalogue.PieceOf(kit.Key, option));
            Assert.Equal(new KitPlace(kit.Key, option), Must(catalogue.KitOf(piece)));
        }
    }

    [Fact]
    public void A_test_piece_put_in_window_is_a_window_under_wall_and_a_wall_rule_matches_it()
    {
        var pane = Panel("ModSkyPane").InFamily(PieceFamilies.Window);
        using var world = Start(TestScenario.Empty.WithPiece(pane));
        var catalogue = world.System.Catalogue;

        Assert.Equal(new[] { PieceFamilies.Window, PieceFamilies.Wall }, Must(catalogue.FamiliesOf(pane.Key)));
        Assert.Equal(PieceFamilies.Wall, Must(catalogue.FamilyOf(pane.Key)));
        Assert.True(world.System.Styles.Matches(new StyleMatch.Family(PieceFamilies.Wall), new StructureRef(new ReferenceId(1), pane.Key, default)));
    }

    [Fact]
    public void A_piece_in_two_kits_lists_both_its_own_kit_first_and_shows_its_display_name()
    {
        var corner = Panel("ModPanelCorner").AlsoIn("ItemKitModTrim", 1).Named("Panel corner, 45°");
        var trim = Must(TestPiece.Create("ModTrim", "ItemKitModTrim", PieceGrid.Large));
        using var world = Start(TestScenario.Empty.WithPiece(trim).WithPiece(corner));
        var catalogue = world.System.Catalogue;

        var entry = Must(catalogue.Piece(corner.Key));

        Assert.Equal(
            new[] { new KitPlace(corner.KitKey, Must(KitOption.Create(0))), new KitPlace(trim.KitKey, Must(KitOption.Create(1))) },
            entry.Kits);
        Assert.Equal(entry.Kits[0], Must(catalogue.KitOf(corner.Key)));
        Assert.Equal("Panel corner, 45°", entry.Name);
        Assert.Equal("ModPanelCorner", entry.PrefabName);
        Assert.Equal(PieceGrid.Large, entry.Grid);
        Assert.Equal(new[] { trim.Key, corner.Key }, Must(catalogue.Kit(trim.KitKey)).Options);
    }

    [Fact]
    public void The_hand_catalogues_view_lists_every_hand_piece_and_a_modded_piece_joins_a_base_kit_at_its_next_option()
    {
        var hand = TestCatalogue.Hand.View;
        var coil = Must(hand.Kit(PrefabKitKey("ItemCableCoil")));
        var thin = Must(TestPiece.Create("ModCableThin", "ItemCableCoil", PieceGrid.Small));
        using var world = Start(TestScenario.Empty.WithPiece(thin));

        Assert.Equal(TestCatalogue.Hand.Count, hand.Pieces.Count);
        Assert.Equal(coil.Options.Count + 1, Must(world.System.Catalogue.Kit(coil.Key)).Options.Count);
        Assert.Equal(new KitPlace(coil.Key, Must(KitOption.Create(coil.Options.Count))), Must(world.System.Catalogue.KitOf(thin.Key)));
        Assert.Equal(thin.Name, Must(world.System.Catalogue.Piece(thin.Key)).Name);
    }

    [Fact]
    public void An_option_another_piece_takes_a_gap_in_a_kit_and_a_bad_second_kit_are_refused_at_start()
    {
        Assert.Equal(ReasonCodes.InvalidValue, Refusal(TestScenario.Empty.WithPiece(Panel("ModA").AtOption(0)).WithPiece(Panel("ModB").AtOption(0))).Code);
        Assert.Equal("kit-option-gap", Refusal(TestScenario.Empty.WithPiece(Panel("ModA").AtOption(1))).Facts["problem"]);
        Assert.Equal(ReasonCodes.InvalidValue, Refusal(TestScenario.Empty.WithPiece(Panel("ModA").AtOption(-1))).Code);
        Assert.Equal("also-in", Refusal(TestScenario.Empty.WithPiece(Panel("ModA").AlsoIn("Item Kit", 0))).Facts["problem"]);
        Assert.Equal(ReasonCodes.InvalidValue, Refusal(TestScenario.Empty.WithPiece(Panel("ModA").AlsoIn("ItemCableCoil", 0))).Code);
    }

    private static KitKey PrefabKitKey(string kit) => Must(TestPiece.Create("Any", kit, PieceGrid.Small)).KitKey;

    private static Reason Refusal(TestScenario scenario)
    {
        var started = TestWorld.Start(scenario);
        if (started.TryGet(out var world))
        {
            world.Dispose();
            throw new Xunit.Sdk.XunitException("the world started");
        }

        started.TryGetError(out var why);
        return why;
    }
}
