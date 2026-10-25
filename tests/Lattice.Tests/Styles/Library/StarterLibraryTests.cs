using System;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Styles.Library;
using static Lattice.Tests.Styles.Library.LibraryBench;

namespace Lattice.Tests.Styles.Library;

/// <summary>Each starter library row: a thing in that state resolves to that row's look.</summary>
public sealed class StarterLibraryTests
{
    public static TheoryData<int> Rows()
    {
        var rows = new TheoryData<int>();
        foreach (LibraryRow row in Enum.GetValues(typeof(LibraryRow)))
            rows.Add((int)row);
        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void A_thing_in_the_rows_state_resolves_to_the_rows_look(int row)
    {
        var look = Starter.Look((LibraryRow)row);
        var resolved = Opinions(Resolve(ThingFor(look.Row)));

        Assert.NotEmpty(Opinions(look.Style));
        foreach (var (property, value) in Opinions(look.Style))
            Assert.Equal(value, resolved[property]);
    }

    [Fact]
    public void Every_row_is_in_the_library_once_and_the_rules_follow_the_looks_in_order()
    {
        Assert.Equal(Enum.GetValues(typeof(LibraryRow)).Cast<LibraryRow>(), Starter.Looks.Select(l => l.Row));
        Assert.Equal(Starter.Looks.Select(l => l.Rule), Starter.Rules);
    }

    [Fact]
    public void A_finish_only_ghost_keeps_its_paint_edge_when_it_passes_and_waits_fails_and_warns_still_win()
    {
        var finish = ThingFor(LibraryRow.Finish);
        var paint = Resolve(finish).Edge;

        Assert.Equal(paint, Resolve(finish with { Finding = FindingKind.Passes }).Edge);
        foreach (var row in new[] { LibraryRow.Waits, LibraryRow.Fails, LibraryRow.Warns })
            Assert.Equal(Starter.Look(row).Rule.Style.Edge, Resolve(finish with { Finding = ThingFor(row).Finding }).Edge);
    }

    [Fact]
    public void A_planned_ghost_is_a_translucent_ghost_fill_in_its_tint_or_planned_at_the_cursor_alpha()
    {
        var look = Resolve(Thing.Planned);

        Assert.Equal(Option.Some(FillMode.Ghost), look.Fill);
        Assert.Equal(Option.Some<Paint>(new Paint.GhostTint(LibraryPalette.Planned)), look.FillPaint);
        Assert.Equal(0.3f, look.FillOpacity.Reduce(Fraction.One).Value);
    }

    [Fact]
    public void State_lives_on_edge_pattern_effect_and_icon_so_only_stale_recolours_the_tinted_fill()
    {
        var recolouring = Starter.Looks.Where(l => l.Style.FillPaint.IsSome).Select(l => l.Row);

        Assert.Equal(new[] { LibraryRow.Planned, LibraryRow.Stale }, recolouring);
    }

    [Fact]
    public void A_failing_ghost_is_red_edged_hatched_pulsing_slowly_with_an_icon_and_flashes_three_times_when_it_starts()
    {
        var look = Resolve(ThingFor(LibraryRow.Fails));

        Assert.Equal(LibraryPalette.Fail, EdgePaint(look));
        Assert.Equal(Option.Some(Pattern.Hatch), look.Pattern);
        Assert.Equal(1.5f, Assert.IsType<EffectLook.Pulse>(look.Effect.Reduce(new EffectLook.Steady())).Period.Seconds);
        Assert.Equal(3, Assert.IsType<EnterLook.Flash>(look.Enter.Reduce(new EnterLook.None())).Count.Count);
        Assert.Equal(Option.Some<IconLook>(new IconLook.Shown(LibraryIcons.Fails)), look.Icon);
    }

    [Fact]
    public void A_hovered_failing_ghost_shows_its_reason_and_a_hovered_waiting_ghost_its_waits_links()
    {
        var fails = Resolve(ThingFor(LibraryRow.FailsHovered));
        var waits = Resolve(ThingFor(LibraryRow.WaitsHovered));

        var label = Assert.IsType<LabelLook.Shown>(fails.Label.Reduce(new LabelLook.None()));
        Assert.IsType<LabelText.FindingReason>(label.Says);
        Assert.Equal(LibraryText.Default, label.Style);
        Assert.Equal(Option.Some(ShowMode.Shown), waits.WaitsLinks);
        Assert.True(Resolve(ThingFor(LibraryRow.Fails)).Label.IsNone);
    }

    [Fact]
    public void A_removal_is_an_unfilled_cross_hatched_overlay_and_a_swaps_old_piece_looks_the_same_but_for_its_icon()
    {
        var remove = Resolve(ThingFor(LibraryRow.Remove));
        var swapOld = Resolve(ThingFor(LibraryRow.SwapOld));

        Assert.Equal(Option.Some(FillMode.None), remove.Fill);
        Assert.Equal(Option.Some(Pattern.CrossHatch), remove.Pattern);
        Assert.Equal(remove with { Icon = swapOld.Icon }, swapOld);
        Assert.Equal(Option.Some<IconLook>(new IconLook.Shown(LibraryIcons.Swap)), swapOld.Icon);
    }

    [Fact]
    public void A_swaps_new_piece_is_the_planned_look_with_the_swap_icon()
    {
        var swapNew = Resolve(ThingFor(LibraryRow.Swap));

        Assert.Equal(Resolve(Thing.Planned) with { Icon = swapNew.Icon }, swapNew);
    }

    [Fact]
    public void A_draft_takes_the_findings_looks_and_a_pending_draft_is_faint_with_no_edge()
    {
        var passingDraft = Resolve(Thing.Planned with { Draft = true, Finding = FindingKind.Passes });
        var pending = Resolve(ThingFor(LibraryRow.Pending));

        Assert.Equal(LibraryPalette.Next, EdgePaint(passingDraft));
        Assert.Equal(0.25f, passingDraft.FillOpacity.Reduce(Fraction.One).Value);
        Assert.Equal(Option.Some<EdgeLook>(new EdgeLook.None()), pending.Edge);
        Assert.Equal(0.12f, pending.FillOpacity.Reduce(Fraction.One).Value);
    }

    [Fact]
    public void Another_players_ghost_is_dimmed_and_edged_in_its_owners_colour_but_structures_and_drafts_are_not()
    {
        var others = Resolve(ThingFor(LibraryRow.Others));
        var structure = Resolve(Thing.Planned with { Mine = false, Structure = true, Part = PartKind.Existing });

        Assert.Equal(0.6f, others.Dim.Reduce(Fraction.One).Value);
        Assert.IsType<Paint.OwnerColour>(Assert.IsType<EdgeLook.Line>(others.Edge.Reduce(new EdgeLook.None())).Paint);
        Assert.True(structure.Dim.IsNone);
    }

    [Fact]
    public void Every_drawn_thing_vanishes_over_400_ms_when_it_leaves()
    {
        foreach (var (_, thing) in States)
            Assert.Equal(0.4f, Resolve(thing).Vanish.Reduce(Duration.Zero).Seconds);
    }

    [Fact]
    public void The_looks_never_name_a_piece_or_family_so_a_modded_piece_gets_the_same_look()
    {
        var leaves = Starter.Rules.SelectMany(r => Leaves(r.Match)).ToList();
        var modded = Must(PieceFamily.Create(Must(ModId.Create("mymod")), "conduits"));

        Assert.DoesNotContain(leaves, l => l is StyleMatch.Family or StyleMatch.Picks);
        foreach (var (_, thing) in States)
            Assert.Equal(Resolve(thing), Resolve(thing with { Family = modded }));
    }

    [Fact]
    public void Every_paint_token_the_library_uses_is_a_published_library_palette_key()
    {
        var published = typeof(LibraryPalette).GetFields().Select(f => (PaletteKey)f.GetValue(null)!).ToHashSet();
        var styles = Starter.Rules.Select(r => r.Style)
            .Concat(Starter.Blueprint.Rules.Select(r => r.Style))
            .Concat(Starter.LabelLayers.SelectMany(l => l.Rules).Select(r => r.Style));
        var tokens = styles.SelectMany(Paints).Select(p => p switch
        {
            Paint.Token t => t.Key,
            Paint.GhostTint g => g.Fallback,
            Paint.FinishColour f => f.Fallback,
            _ => (PaletteKey?)null,
        }).OfType<PaletteKey>();

        Assert.All(tokens, t => Assert.Contains(t, published));
    }

    private static PaletteKey EdgePaint(Style look) =>
        Assert.IsType<Paint.Token>(Assert.IsType<EdgeLook.Line>(look.Edge.Reduce(new EdgeLook.None())).Paint).Key;

}
