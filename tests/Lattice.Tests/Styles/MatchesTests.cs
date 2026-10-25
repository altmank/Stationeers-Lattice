using System;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.TestKit.Allocations;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// Matches against built structures, structure overlays, and Explain's cost. Piece facets
/// read the family once per piece; set membership is live; ghost-only cases are false; a call allocates nothing.
/// </summary>
public sealed class MatchesTests
{
    private static StructureRef Built(StyleBench bench, string piece, int id, Cell at) => new(new ReferenceId(id), bench.T.Catalogue.Named(piece).Key, at);

    [Fact]
    public void A_modded_family_matches_every_structure_of_its_piece_and_a_family_registered_later_is_seen()
    {
        var bench = new StyleBench();
        var conduits = FamilyOf("conduits");
        Assert.False(bench.Styles.Matches(new StyleMatch.Family(conduits), Built(bench, Modded, 1, new Cell(5, 5, 5))));

        bench.Looks.Family(Modded, conduits);

        for (var i = 0; i < 20; i++)
            Assert.True(bench.Styles.Matches(new StyleMatch.Family(conduits), Built(bench, Modded, i + 1, new Cell(5 * i, 5, 5))));
        Assert.False(bench.Styles.Matches(new StyleMatch.Family(PieceFamilies.Cable), Built(bench, Modded, 99, new Cell(5, 5, 5))));
    }

    [Fact]
    public void Piece_facets_hold_and_ghost_only_cases_are_false()
    {
        var bench = new StyleBench();
        var frame = Built(bench, "StructureFrame", 1, new Cell(10, 10, 10));
        var piece = frame.Piece;

        Assert.True(bench.Styles.Matches(new StyleMatch.Everything(), frame));
        Assert.True(bench.Styles.Matches(new StyleMatch.Structure(), frame));
        Assert.True(bench.Styles.Matches(new StyleMatch.Part(PartKind.Existing), frame));
        Assert.True(bench.Styles.Matches(new StyleMatch.Picks(new Selector.OfPiece(piece)), frame));
        Assert.False(bench.Styles.Matches(new StyleMatch.Part(PartKind.Placement), frame));
        Assert.False(bench.Styles.Matches(new StyleMatch.Picks(new Selector.Group(Must(GroupName.Create("power")))), frame));
        foreach (var ghostOnly in new StyleMatch[]
        {
            new StyleMatch.State(StateKind.Planned),
            new StyleMatch.Finding(FindingKind.Unchecked, Option<ReasonCode>.None),
            new StyleMatch.Status(StatusKind.None, Option<ExecutorId>.None),
            new StyleMatch.Draft(),
            new StyleMatch.Pending(),
            new StyleMatch.Mine(),
            new StyleMatch.Tinted(),
            new StyleMatch.Intent(IntentKind.Build),
        })
            Assert.False(bench.Styles.Matches(ghostOnly, frame), ghostOnly.ToString());
        Assert.True(bench.Styles.Matches(new StyleMatch.Not(new StyleMatch.Mine()), frame));
        Assert.True(bench.Styles.Matches(new StyleMatch.All(ValueList.From(new StyleMatch[] { new StyleMatch.Structure(), new StyleMatch.Part(PartKind.Existing) })), frame));
        Assert.False(bench.Styles.Matches(new StyleMatch.Any(ValueList<StyleMatch>.Empty), frame));
    }

    [Fact]
    public void Structure_set_membership_is_read_live_by_id_or_by_piece_and_cell()
    {
        var bench = new StyleBench();
        var frame = Built(bench, "StructureFrame", 1, new Cell(10, 10, 10));
        var inspected = SetId("inspected");
        var match = new StyleMatch.InSet(inspected);
        Assert.False(bench.Styles.Matches(match, frame));

        Must(bench.Styles.StructureSet(inspected)).Put(frame);
        Assert.True(bench.Styles.Matches(match, frame));
        Assert.True(bench.Styles.Matches(match, frame with { Id = new ReferenceId(500) }));

        Must(bench.Styles.StructureSet(inspected)).Clear();
        Assert.False(bench.Styles.Matches(match, frame));
    }

    [Fact]
    public void A_match_call_allocates_nothing()
    {
        var bench = new StyleBench();
        bench.Looks.Family("StructureFrame", PieceFamilies.Frame);
        var frame = Built(bench, "StructureFrame", 1, new Cell(10, 10, 10));
        var match = new StyleMatch.All(ValueList.From(new StyleMatch[]
        {
            new StyleMatch.Family(PieceFamilies.Frame),
            new StyleMatch.Not(new StyleMatch.InSet(SetId("hidden"))),
        }));

        Allocations.AssertNone(() => bench.Styles.Matches(match, frame), "Matches");
    }

    [Fact]
    public void A_structure_in_a_set_is_drawn_as_an_overlay_while_it_is_a_member()
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("overlays", StyleBand.Selection, 0, Rule(new StyleMatch.Structure(), Edge(LineStyle.Solid, LibraryPalette.Select))));
        var frame = Built(bench, "StructureFrame", 1, new Cell(10, 10, 10));
        var set = Must(bench.Styles.StructureSet(SetId("inspected")));

        set.Put(frame);
        var overlay = Assert.Single(Overlays(bench));
        Assert.Equal(PartKind.Existing, overlay.Part);
        Assert.IsType<EdgeLook.Line>(bench.Styles.Look(overlay.Signature).Style.Edge.Reduce(new EdgeLook.None()));

        set.Clear();
        Assert.Empty(Overlays(bench));
    }

    [Fact]
    public void A_highlight_of_structures_draws_their_overlays_until_it_ends()
    {
        var bench = new StyleBench();
        var frame = Built(bench, "StructureFrame", 1, new Cell(10, 10, 10));

        var highlight = Must(bench.Styles.Highlight(new HighlightRequest(new HighlightTargets.Structures(ValueList.Of(frame)),
            new Style { Glow = Option.Some<GlowLook>(new GlowLook.Lit(new Paint.Token(LibraryPalette.Fail), Fraction.One)) }, Must(Duration.Create(3f)))));
        var overlay = Assert.Single(Overlays(bench));
        Assert.True(bench.Styles.Look(overlay.Signature).Glow.A > 0);

        highlight.Dispose();
        Assert.Empty(Overlays(bench));
    }

    [Fact]
    public void Explain_costs_one_resolve_after_a_toggle_and_explains_a_removal_by_its_overlay()
    {
        var bench = new StyleBench();
        var layer = bench.Add(LayerOf("removals", StyleBand.Theme, 0, Rule(new StyleMatch.Intent(IntentKind.Remove), Edge(LineStyle.Dashed, LibraryPalette.Remove))));
        var removal = Assert.Single(bench.Place(Lattice.Tests.Board.TestBoard.Draft(new Intent.Remove(Built(bench, "StructureFrame", 7, new Cell(10, 10, 10))))).Added);
        layer.Disable();
        layer.Enable();
        var resolves = bench.Styles.Resolves;

        var story = bench.Story(removal.Id);

        Assert.Equal(1, bench.Styles.Resolves - resolves);
        Assert.Equal(new StyleSource.Layer(StyleBand.Theme, Layer("removals"), 0), story.Sources[StyleProperty.Edge]);
    }

    private static System.Collections.Generic.List<Thing> Overlays(StyleBench bench)
    {
        var found = new System.Collections.Generic.List<Thing>();
        for (var b = 0; b < bench.Styles.Plan.BatchCount; b++)
        {
            var batch = bench.Styles.Plan.BatchAt(b);
            for (var i = 0; i < batch.Count; i++)
                if (batch[i].Thing.Kind == ThingKind.Structure)
                    found.Add(batch[i].Thing);
        }

        return found;
    }
}
