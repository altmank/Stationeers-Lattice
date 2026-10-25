using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Styles.Library;
using static Lattice.Tests.Styles.Library.LibraryBench;

namespace Lattice.Tests.Styles.Library;

/// <summary>
/// Every state reads without colour alone: every pair of library states
/// differs on a channel besides hue, under each library palette. Channels: presence, fill mode, edge line and width,
/// pattern, glow, effect, enter, icon, waits links, label, opacity (by 0.1 or more), and colour lightness (a contrast
/// of 3:1 or more, the WCAG figure for non-text marks). Hue alone never counts.
/// </summary>
public sealed class StateChannelTests
{
    private const float OpacityStep = 0.1f;
    private const double LightnessContrast = 3.0;

    public static TheoryData<string> PaletteIds()
    {
        var ids = new TheoryData<string>();
        foreach (var palette in Starter.Palettes)
            ids.Add(palette.Id.Value);
        return ids;
    }

    [Theory]
    [MemberData(nameof(PaletteIds))]
    public void Every_state_pair_differs_on_a_channel_besides_hue(string paletteId)
    {
        var palette = PaletteNamed(paletteId);
        var alike = new List<string>();

        for (var i = 0; i < States.Count; i++)
            for (var j = i + 1; j < States.Count; j++)
                if (!Differ(Resolve(States[i].Thing), Resolve(States[j].Thing), palette))
                    alike.Add($"{States[i].Name} / {States[j].Name}");

        Assert.Empty(alike);
    }

    [Theory]
    [MemberData(nameof(PaletteIds))]
    public void Every_state_pair_differs_on_a_channel_besides_hue_on_the_real_styles(string paletteId)
    {
        var palette = PaletteNamed(paletteId);
        var states = RealStates.Select(s => (s.Name, Look: LibraryResolveTests.RealLook(s.Row))).ToList();
        var alike = new List<string>();

        for (var i = 0; i < states.Count; i++)
            for (var j = i + 1; j < states.Count; j++)
                if (!Differ(states[i].Look, states[j].Look, palette))
                    alike.Add($"{states[i].Name} / {states[j].Name}");

        Assert.Empty(alike);
    }

    [Fact]
    public void Two_looks_that_differ_only_in_hue_count_as_alike()
    {
        var palette = PaletteNamed("default");
        var look = Resolve(Thing.Planned with { Finding = FindingKind.WillFail });
        var blueEdge = look with
        {
            Edge = Option.Some<EdgeLook>(new EdgeLook.Line(LineStyle.Solid, Must(Pixels.Create(2f)), new Paint.Fixed(new Rgba(0, 0, 255, 255)))),
        };
        var redEdge = look with
        {
            Edge = Option.Some<EdgeLook>(new EdgeLook.Line(LineStyle.Solid, Must(Pixels.Create(2f)), new Paint.Fixed(new Rgba(255, 0, 0, 255)))),
        };

        Assert.False(Differ(redEdge, blueEdge, palette));
    }

    private static (string Name, LibraryRow Row)[] RealStates =>
    [
        ("unchecked", LibraryRow.Unchecked), ("next", LibraryRow.Next), ("waits", LibraryRow.Waits), ("fails", LibraryRow.Fails),
        ("warns", LibraryRow.Warns), ("started", LibraryRow.Started), ("stale", LibraryRow.Stale), ("remove", LibraryRow.Remove),
        ("swap new", LibraryRow.Swap), ("swap old", LibraryRow.SwapOld), ("finished build", LibraryRow.Finished), ("finish", LibraryRow.Finish), ("draft", LibraryRow.Draft),
        ("pending", LibraryRow.Pending), ("working", LibraryRow.Working), ("waiting", LibraryRow.Waiting), ("paused", LibraryRow.Paused),
        ("others", LibraryRow.Others), ("selected", LibraryRow.Selected), ("hovered", LibraryRow.Hovered),
    ];

    private static Palette PaletteNamed(string id)
    {
        foreach (var palette in Starter.Palettes)
            if (palette.Id.Value == id)
                return palette;
        throw new ArgumentException(id);
    }

    private static bool Differ(Style a, Style b, Palette palette) =>
        Shape(a) != Shape(b)
        || Math.Abs(Opacity(a) - Opacity(b)) >= OpacityStep
        || Contrast(EdgeColour(a, palette), EdgeColour(b, palette)) >= LightnessContrast
        || Contrast(FillColour(a, palette), FillColour(b, palette)) >= LightnessContrast;

    private static ShapeChannels Shape(Style s) => new(
        s.Presence.Reduce(Presence.Drawn),
        s.Fill.Reduce(FillMode.Ghost),
        s.Edge.Reduce(new EdgeLook.None()) switch
        {
            EdgeLook.Line line => $"{line.Style} {line.Width.Value}",
            _ => "none",
        },
        s.Pattern.Reduce(Pattern.None),
        s.Glow.Reduce(new GlowLook.None()) is GlowLook.Lit,
        s.Effect.Reduce(new EffectLook.Steady()) switch
        {
            EffectLook.Pulse p => $"pulse {p.Period.Seconds}",
            EffectLook.Blink b => $"blink {b.Period.Seconds}",
            _ => "steady",
        },
        s.Enter.Reduce(new EnterLook.None()) is EnterLook.Flash,
        s.Icon.Reduce(new IconLook.None()) switch
        {
            IconLook.Shown shown => shown.Icon.Value,
            _ => "",
        },
        s.WaitsLinks.Reduce(ShowMode.None),
        s.Label.Reduce(new LabelLook.None()) is LabelLook.Shown);

    private static float Opacity(Style s) =>
        s.Fill.Reduce(FillMode.Ghost) == FillMode.None ? 0f : s.FillOpacity.Reduce(Must(Fraction.Create(0.3f))).Value * s.Dim.Reduce(Fraction.One).Value;

    private static Rgba? EdgeColour(Style s, Palette palette) =>
        s.Edge.Reduce(new EdgeLook.None()) is EdgeLook.Line line ? ColourOf(line.Paint, palette) : null;

    private static Rgba? FillColour(Style s, Palette palette) =>
        s.Fill.Reduce(FillMode.Ghost) == FillMode.None ? null : ColourOf(s.FillPaint.Reduce(new Paint.Token(LibraryPalette.Planned)), palette);

    private static double Contrast(Rgba? a, Rgba? b)
    {
        if (a is not { } x || b is not { } y)
            return 1.0;
        var (la, lb) = (Luminance(x), Luminance(y));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Rgba c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Linear(byte channel)
    {
        var v = channel / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private sealed record ShapeChannels(
        Presence Presence, FillMode Fill, string Edge, Pattern Pattern, bool Glow, string Effect, bool Enter, string Icon,
        ShowMode WaitsLinks, bool Label);
}
