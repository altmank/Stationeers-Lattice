using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Styles.Library;

namespace Lattice.Game.Live;

/// <summary>
/// One row of the look grid: a library state, the library look that shows it, and the match whose rule in a candidate
/// theme restyles it (none: the theme's base look alone).
/// </summary>
internal sealed record LookRow(string Name, LibraryRow Library, StyleMatch? Theme);

/// <summary>
/// One column of the look grid: a theme, the style set its ghosts sit in, and the rules it draws them with. The set
/// makes the column's rules reach only the column's ghosts, so every theme shows side by side in one frame.
/// </summary>
internal sealed record LookColumn(string Name, StyleSetId Set, StyleLayerDescriptor Layer);

/// <summary>
/// The look grid for picking a theme: every library state in rows, the library and each linked candidate theme in
/// columns. A state is shown by putting its ghosts in the row's set, so a state that needs a
/// finding, an executor or another player shows without them; the column's layer (Highlight band, above every real
/// look) lays the theme's base look and the row's look over those ghosts. Palette tokens are resolved to fixed colours
/// here, since one palette is in force at a time and the columns each need their own.
/// </summary>
internal static class RenderLooks
{
    private static readonly StyleMatch Existing = new StyleMatch.Part(PartKind.Existing);

    public static IReadOnlyList<LookRow> Rows { get; } = new[]
    {
        new LookRow("planned", LibraryRow.Planned, null),
        new LookRow("started", LibraryRow.Started, new StyleMatch.State(StateKind.Started)),
        new LookRow("unchecked", LibraryRow.Unchecked, Finding(FindingKind.Unchecked)),
        new LookRow("next", LibraryRow.Next, Finding(FindingKind.Passes)),
        new LookRow("waits", LibraryRow.Waits, Finding(FindingKind.WaitsFor)),
        new LookRow("fails", LibraryRow.Fails, Finding(FindingKind.WillFail)),
        new LookRow("warns", LibraryRow.Warns, Finding(FindingKind.Warns)),
        new LookRow("remove", LibraryRow.Remove, new StyleMatch.Intent(IntentKind.Remove)),
        new LookRow("swap-old", LibraryRow.SwapOld, All(new StyleMatch.Intent(IntentKind.Swap), Existing)),
        new LookRow("swap-new", LibraryRow.Swap, All(new StyleMatch.Intent(IntentKind.Swap), new StyleMatch.Part(PartKind.Placement))),
        new LookRow("finish-only", LibraryRow.Finish, new StyleMatch.Intent(IntentKind.FinishOnly)),
        new LookRow("stale", LibraryRow.Stale, new StyleMatch.State(StateKind.Stale)),
        new LookRow("draft", LibraryRow.Draft, new StyleMatch.Draft()),
        new LookRow("pending", LibraryRow.Pending, new StyleMatch.Pending()),
        new LookRow("working", LibraryRow.Working, Status(StatusKind.Working)),
        new LookRow("waiting", LibraryRow.Waiting, Status(StatusKind.Waiting)),
        new LookRow("paused", LibraryRow.Paused, Status(StatusKind.Paused)),
        new LookRow("others", LibraryRow.Others, new StyleMatch.Not(new StyleMatch.Mine())),
        new LookRow("hovered", LibraryRow.Hovered, new StyleMatch.InSet(LibrarySets.Hovered)),
        new LookRow("selected", LibraryRow.Selected, new StyleMatch.InSet(LibrarySets.Selected)),
    };

    /// <summary>The row's set: its ghosts take the row's look in every column.</summary>
    public static StyleSetId RowSet(LookRow row) => Must(StyleSetId.Create(LatticeIdentity.Mod, "live-row-" + row.Name));

    /// <summary>The library column, then one column per linked candidate theme (none when no theme file is linked).</summary>
    public static IReadOnlyList<LookColumn> Columns()
    {
        var library = new StarterLibrary();
        var columns = new List<LookColumn>(4) { Library(library) };
#if LOOK_THEMES
        var order = 1;
        foreach (var theme in CandidateThemes.All)
            columns.Add(Theme(theme, library, order++));
#endif
        return columns;
    }

    private static LookColumn Library(StarterLibrary library)
    {
        var colours = library.Default.Colours;
        Rgba Colour(PaletteKey key) => colours.TryGetValue(key, out var c) ? c : colours[LibraryPalette.Planned];
        var set = ColumnSet("library");
        var planned = library.Look(LibraryRow.Planned).Style;
        // The library's Planned look sets the fill only; the rest of the base is every other property at its quiet value,
        // so a row shows exactly what its library look adds.
        var basis = Quiet with { Fill = planned.Fill, FillPaint = planned.FillPaint, FillOpacity = planned.FillOpacity };
        var rules = new List<StyleRule>(Rows.Count + 1) { new(new StyleMatch.InSet(set), Concrete(basis, Colour)) };
        foreach (var row in Rows)
            rules.Add(new StyleRule(InRow(set, row), Concrete(library.Look(row.Library).Style, Colour)));
        return new LookColumn("library", set, Layer("library", 0, rules));
    }

#if LOOK_THEMES
    private static LookColumn Theme(CandidateTheme theme, StarterLibrary library, int order)
    {
        var palette = theme.Look.Palette.TryGet(out var p) ? p.Colours : ValueMap<PaletteKey, Rgba>.Empty;
        var fallback = library.Default.Colours;
        Rgba Colour(PaletteKey key) =>
            palette.TryGetValue(key, out var c) ? c : fallback.TryGetValue(key, out var f) ? f : fallback[LibraryPalette.Planned];
        var set = ColumnSet(theme.Name);
        var themeRules = theme.Look.Rules;
        var rules = new List<StyleRule>(Rows.Count + 2) { new(new StyleMatch.InSet(set), Concrete(Quiet, Colour)) };
        foreach (var rule in themeRules)
            if (rule.Match is StyleMatch.Everything)
                rules.Add(new StyleRule(new StyleMatch.InSet(set), Concrete(rule.Style, Colour)));
        foreach (var row in Rows)
        {
            if (row.Theme is null)
                continue;
            foreach (var rule in themeRules)
                if (rule.Match == row.Theme)
                    rules.Add(new StyleRule(InRow(set, row), Concrete(rule.Style, Colour)));
            foreach (var effect in theme.Effects)
                foreach (var rule in effect.Rules)
                    if (rule.Match == row.Theme)
                        rules.Add(new StyleRule(InRow(set, row), Concrete(rule.Style, Colour)));
        }

        return new LookColumn(theme.Name, set, Layer(theme.Name, order, rules));
    }
#endif

    /// <summary>Every look property at its quiet value: the base under a column, so no real look shows through.</summary>
    private static Style Quiet { get; } = new()
    {
        Edge = Option.Some<EdgeLook>(new EdgeLook.None()),
        Pattern = Option.Some(Pattern.None),
        Glow = Option.Some<GlowLook>(new GlowLook.None()),
        Effect = Option.Some<EffectLook>(new EffectLook.Steady()),
        Enter = Option.Some<EnterLook>(new EnterLook.None()),
        Icon = Option.Some<IconLook>(new IconLook.None()),
        SeeThrough = Option.Some<SeeThroughLook>(new SeeThroughLook.None()),
        Label = Option.Some<LabelLook>(new LabelLook.None()),
        Dim = Option.Some(Fraction.One),
    };

    /// <summary>The style with every palette token, ghost tint and finish colour as the column's fixed colour.</summary>
    public static Style Concrete(Style style, Func<PaletteKey, Rgba> colour)
    {
        Paint Fix(Paint paint) => paint switch
        {
            Paint.Token token => new Paint.Fixed(colour(token.Key)),
            Paint.GhostTint tint => new Paint.Fixed(colour(tint.Fallback)),
            Paint.FinishColour finish => new Paint.Fixed(colour(finish.Fallback)),
            _ => paint,
        };

        return style with
        {
            FillPaint = style.FillPaint.TryGet(out var fill) ? Option.Some(Fix(fill)) : style.FillPaint,
            Edge = style.Edge.TryGet(out var edge) && edge is EdgeLook.Line line ? Option.Some<EdgeLook>(line with { Paint = Fix(line.Paint) }) : style.Edge,
            Glow = style.Glow.TryGet(out var glow) && glow is GlowLook.Lit lit ? Option.Some<GlowLook>(lit with { Paint = Fix(lit.Paint) }) : style.Glow,
        };
    }

    private static StyleLayerDescriptor Layer(string name, int order, List<StyleRule> rules) =>
        new(Must(StyleLayerId.Create(LatticeIdentity.Mod, "live-look-" + name)), StyleBand.Highlight, order, ValueList.From(rules));

    private static StyleSetId ColumnSet(string name) => Must(StyleSetId.Create(LatticeIdentity.Mod, "live-column-" + name));

    private static StyleMatch InRow(StyleSetId column, LookRow row) => All(new StyleMatch.InSet(column), new StyleMatch.InSet(RowSet(row)));

    private static StyleMatch Finding(FindingKind kind) => new StyleMatch.Finding(kind, Option<ReasonCode>.None);

    private static StyleMatch Status(StatusKind kind) => new StyleMatch.Status(kind, Option<ExecutorId>.None);

    private static StyleMatch All(StyleMatch a, StyleMatch b) => new StyleMatch.All(ValueList.From(new[] { a, b }));

    private static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.TryGet(out var value) ? value : throw new InvalidOperationException(result.ToString());
}
