using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Styles;

/// <summary>Where a rule sits in the cascade, lowest first: Lattice's library, the five nameable bands, Lattice's inspect overlay.</summary>
internal static class Bands
{
    public const int Library = 1;

    public const int Inspect = 7;

    public static bool IsNameable(StyleBand band) => band is StyleBand.Meaning or StyleBand.Theme or StyleBand.View or StyleBand.Selection or StyleBand.Highlight;
}

/// <summary>One rule in a generation's program, with what orders it and what turns it off.</summary>
/// <param name="Band">Its band's place (<see cref="Bands"/>, or a <see cref="StyleBand"/> value).</param>
/// <param name="Order">Its layer's order within the band.</param>
/// <param name="Registration">When its layer or highlight was registered, ascending.</param>
/// <param name="Position">Its position in its layer.</param>
/// <param name="Match">Its match, compiled to the generation's bits.</param>
/// <param name="Style">Its opinions.</param>
/// <param name="Source">What <see cref="IGhostStyles.Explain"/> names for it.</param>
/// <param name="Layer">Its layer; null for a highlight's rule.</param>
/// <param name="Highlight">Its highlight; null for a layer's rule.</param>
internal sealed record ProgramRule(
    int Band, int Order, long Registration, int Position, Compiled Match, Style Style, StyleSource Source, LayerEntry? Layer, HighlightEntry? Highlight)
{
    /// <summary>Whether the rule applies now: its layer enabled and not disposed, or its highlight not ended.</summary>
    public bool Active => Layer is not null ? Layer.Enabled && !Layer.Disposed : Highlight is not null && !Highlight.Ended;

    public bool RunsBefore(ProgramRule other) =>
        Band != other.Band ? Band < other.Band
        : Order != other.Order ? Order < other.Order
        : Registration != other.Registration ? Registration < other.Registration
        : Position < other.Position;
}

/// <summary>A generation's rules in cascade order: (band, layer order, registration, rule position), lowest first.</summary>
internal sealed class RuleProgram
{
    private readonly List<ProgramRule> _rules;

    private RuleProgram(List<ProgramRule> rules) => _rules = rules;

    public static RuleProgram Empty => new(new List<ProgramRule>());

    public IReadOnlyList<ProgramRule> Rules => _rules;

    /// <summary>The rules of these layers and highlights, compiled to the generation's bits (new leaves get bits).</summary>
    public static RuleProgram Of(Generation generation, IReadOnlyList<LayerEntry> layers, IReadOnlyList<HighlightEntry> highlights, LeafBook leaves)
    {
        var rules = new List<ProgramRule>(64);
        foreach (var layer in layers)
            if (!layer.Disposed)
                for (var i = 0; i < layer.Descriptor.Rules.Count; i++)
                {
                    var rule = layer.Descriptor.Rules[i];
                    rules.Add(new ProgramRule(layer.Band, layer.Descriptor.Order, layer.Registration, i,
                        Compile(rule.Match, generation, leaves), rule.Style, layer.Source(i), layer, null));
                }

        foreach (var highlight in highlights)
            if (!highlight.Ended)
                rules.Add(HighlightRule(generation, highlight));
        rules.Sort(static (a, b) => a.RunsBefore(b) ? -1 : b.RunsBefore(a) ? 1 : 0);
        return new RuleProgram(rules);
    }

    /// <summary>Adds a highlight's rule in its place: a highlight's leaf reads false for every thing but its targets.</summary>
    public void Insert(Generation generation, HighlightEntry highlight)
    {
        var rule = HighlightRule(generation, highlight);
        var at = _rules.Count;
        while (at > 0 && rule.RunsBefore(_rules[at - 1]))
            at--;
        _rules.Insert(at, rule);
    }

    /// <summary>Takes an ended highlight's rule out.</summary>
    public void Remove(HighlightEntry highlight)
    {
        for (var i = 0; i < _rules.Count; i++)
            if (ReferenceEquals(_rules[i].Highlight, highlight))
            {
                _rules.RemoveAt(i);
                return;
            }
    }

    private static ProgramRule HighlightRule(Generation generation, HighlightEntry highlight) =>
        new((int)StyleBand.Highlight, 0, highlight.Registration, 0, new Compiled.Bit(generation.BitOf(highlight.Leaf)),
            highlight.Style, new StyleSource.Highlight(), null, highlight);

    /// <summary>
    /// A match compiled to bits. <c>Everything</c> holds, a case this Lattice does not know never does. With
    /// <paramref name="known"/>, no new bits are given: a leaf the generation has no bit for reads false.
    /// </summary>
    public static Compiled Compile(StyleMatch match, Generation generation, LeafBook leaves, bool known = false) => match switch
    {
        StyleMatch.Everything => Compiled.True,
        StyleMatch.All all => all.Of.Count == 0 ? Compiled.True : new Compiled.All(CompileEach(all.Of, generation, leaves, known)),
        StyleMatch.Any any => any.Of.Count == 0 ? Compiled.False : new Compiled.Any(CompileEach(any.Of, generation, leaves, known)),
        StyleMatch.Not not => new Compiled.Not(Compile(not.Match, generation, leaves, known)),
        _ when Facets.IsLeaf(match) => LeafBit(generation, leaves.Find(match), known),
        _ => Compiled.False,
    };

    private static Compiled LeafBit(Generation generation, Leaf leaf, bool known)
    {
        if (!known)
            return new Compiled.Bit(generation.BitOf(leaf));
        return generation.TryBit(leaf, out var bit) ? new Compiled.Bit(bit) : Compiled.False;
    }

    private static Compiled[] CompileEach(ValueList<StyleMatch> matches, Generation generation, LeafBook leaves, bool known)
    {
        var parts = new Compiled[matches.Count];
        for (var i = 0; i < parts.Length; i++)
            parts[i] = Compile(matches[i], generation, leaves, known);
        return parts;
    }
}

/// <summary>
/// The match leaves every live layer uses, counted per use, interned by value. A layer's rules take their leaves when
/// it is added and give them back when it is disposed; enabling and disabling keeps them, so a toggle never
/// re-signatures.
/// </summary>
internal sealed class LeafBook
{
    private readonly Dictionary<StyleMatch, Leaf> _leaves = new();

    public int Count => _leaves.Count;

    /// <summary>The leaf for a match leaf, made on first ask (with no uses).</summary>
    public Leaf Find(StyleMatch match)
    {
        if (!_leaves.TryGetValue(match, out var leaf))
            _leaves[match] = leaf = Leaf.Of(match);
        return leaf;
    }

    public void Take(StyleMatch match) => Tally(match, +1);

    public void Give(StyleMatch match) => Tally(match, -1);

    /// <summary>Forgets leaves no rule uses.</summary>
    public void Forget()
    {
        List<StyleMatch>? unused = null;
        foreach (var pair in _leaves)
            if (pair.Value.Retired)
                (unused ??= new List<StyleMatch>()).Add(pair.Key);
        if (unused is null)
            return;
        foreach (var match in unused)
            _leaves.Remove(match);
    }

    private void Tally(StyleMatch match, int by)
    {
        switch (match)
        {
            case StyleMatch.All all:
                foreach (var part in all.Of)
                    Tally(part, by);
                break;
            case StyleMatch.Any any:
                foreach (var part in any.Of)
                    Tally(part, by);
                break;
            case StyleMatch.Not not:
                Tally(not.Match, by);
                break;
            default:
                if (Facets.IsLeaf(match))
                    Find(match).Uses += by;
                break;
        }
    }
}

/// <summary>
/// A look after the cascade: every property set, where each came from, and the colours its paints turn into under the
/// palette in force. Shared by every thing with the same signature.
/// </summary>
internal sealed class ResolvedLook(Style style, StyleSource[] sources, Rgba fill, Rgba edge, Rgba glow)
{
    public Style Style { get; } = style;

    /// <summary>Per <see cref="StyleProperty"/> value (index 0 unused).</summary>
    public StyleSource[] Sources { get; } = sources;

    /// <summary>The fill colour with the fill opacity and dim applied.</summary>
    public Rgba Fill { get; } = fill;

    /// <summary>The edge colour with dim applied; clear for no edge.</summary>
    public Rgba Edge { get; } = edge;

    /// <summary>The glow colour with its strength and dim applied; clear for no glow.</summary>
    public Rgba Glow { get; } = glow;

    public bool Hidden => Style.Presence.Reduce(Presence.Drawn) == Presence.Hidden;

    public bool Pickable => !Hidden && Style.Pickable.Reduce(Api.Pickable.Yes) == Api.Pickable.Yes;

    /// <summary>Whether drawing it changes over time: an ease, an effect or an entry flash.</summary>
    public bool Animated { get; } =
        style.Ease.Reduce(Duration.Zero).Seconds > 0f
        || style.Effect.Reduce(Steady) is not EffectLook.Steady
        || style.Enter.Reduce(NoEnter) is not EnterLook.None;

    public Option<LabelLook.Shown> Label => Style.Label.TryGet(out var label) && label is LabelLook.Shown shown
        ? Option.Some(shown)
        : Option<LabelLook.Shown>.None;

    private static readonly EffectLook Steady = new EffectLook.Steady();

    private static readonly EnterLook NoEnter = new EnterLook.None();
}

/// <summary>The palette in force and the motion, worked out from the enabled layers in cascade order.</summary>
internal sealed class PaletteInForce
{
    private readonly Dictionary<PaletteKey, Rgba> _colours = new();
    private readonly List<Rgba> _ramp = new(16);
    private readonly Palette? _base;
    private readonly Func<PaletteId, Option<Palette>> _library;
    private readonly IPaintColours _paints;

    /// <summary>
    /// A palette in force over <paramref name="basis"/> (the starter library's default; none: the stand-in default), with
    /// <paramref name="library"/> giving the palettes a layer's palette may name as its base, and the game's paint colours.
    /// </summary>
    public PaletteInForce(Palette? basis = null, Func<PaletteId, Option<Palette>>? library = null, IPaintColours? paints = null)
    {
        _base = basis;
        _library = library ?? NoLibrary;
        _paints = paints ?? new NoPaintColours();
        Overlay(Array.Empty<LayerEntry>());
    }

    /// <summary>Whether a palette's base, if it names one, is a palette Lattice holds.</summary>
    public bool Knows(Palette palette) => !palette.Base.TryGet(out var id) || _library(id).IsSome;

    private static Option<Palette> NoLibrary(PaletteId id) => Option<Palette>.None;

    public Motion Motion { get; private set; } = Motion.Full;

    /// <summary>Lattice's default palette overlaid, key by key, by each enabled layer's palette, lowest first.</summary>
    public void Overlay(IReadOnlyList<LayerEntry> inCascadeOrder)
    {
        _colours.Clear();
        _ramp.Clear();
        if (_base is { } basis)
        {
            foreach (var pair in basis.Colours)
                _colours[pair.Key] = pair.Value;
            _ramp.AddRange(basis.OwnerRamp);
        }
        else
        {
            foreach (var pair in DefaultPalette.Colours)
                _colours[pair.Key] = pair.Value;
            _ramp.AddRange(DefaultPalette.OwnerRamp);
        }

        Motion = Motion.Full;
        foreach (var layer in inCascadeOrder)
        {
            if (!layer.Enabled || layer.Disposed)
                continue;
            if (layer.Descriptor.Palette.TryGet(out var palette))
            {
                if (palette.Base.TryGet(out var baseId) && _library(baseId).TryGet(out var under))
                    Lay(under);
                foreach (var pair in palette.Colours)
                    _colours[pair.Key] = pair.Value;
                if (palette.OwnerRamp.Count > 0)
                {
                    _ramp.Clear();
                    _ramp.AddRange(palette.OwnerRamp);
                }
            }

            if (layer.Descriptor.Motion.TryGet(out var motion) && motion is Motion.Full or Motion.Reduced or Motion.Off)
                Motion = motion;
        }
    }

    private void Lay(Palette palette)
    {
        foreach (var pair in palette.Colours)
            _colours[pair.Key] = pair.Value;
        if (palette.OwnerRamp.Count > 0)
        {
            _ramp.Clear();
            _ramp.AddRange(palette.OwnerRamp);
        }
    }

    /// <summary>A paint's colour for a thing with this signature; an unknown token takes the <c>planned</c> colour.</summary>
    public Rgba Colour(Paint paint, Signature signature) => paint switch
    {
        Paint.Fixed fixedColour => fixedColour.Colour,
        Paint.Token token => Token(token.Key),
        Paint.GhostTint tint => signature.Tint.TryGet(out var own) ? new Rgba(own.R, own.G, own.B, own.A) : Token(tint.Fallback),
        Paint.OwnerColour => signature.Owner >= 0 && _ramp.Count > 0 ? _ramp[signature.Owner % _ramp.Count] : Token(LibraryPalette.Planned),
        Paint.FinishColour finish => FinishPaint(signature).TryGet(out var shown) ? shown : Token(finish.Fallback),
        _ => Token(LibraryPalette.Planned),
    };

    private Option<Rgba> FinishPaint(Signature signature) =>
        signature.Finish >= 0 && PaintColour.Create(signature.Finish).TryGet(out var paint) ? _paints.Colour(paint) : Option<Rgba>.None;

    private Rgba Token(PaletteKey key) =>
        _colours.TryGetValue(key, out var colour) ? colour
        : _colours.TryGetValue(LibraryPalette.Planned, out var planned) ? planned
        : default;
}

/// <summary>
/// The base palette when styles run without the starter library (style tests over a bare cascade): a colour for every
/// library token and an owner ramp. A world's styles take the starter library's default palette instead.
/// </summary>
internal static class DefaultPalette
{
    public static IReadOnlyList<KeyValuePair<PaletteKey, Rgba>> Colours { get; } = new[]
    {
        Pair(LibraryPalette.Planned, new Rgba(80, 150, 255, 255)),
        Pair(LibraryPalette.Next, new Rgba(90, 220, 110, 255)),
        Pair(LibraryPalette.Waits, new Rgba(170, 170, 190, 255)),
        Pair(LibraryPalette.Fail, new Rgba(235, 70, 60, 255)),
        Pair(LibraryPalette.Warn, new Rgba(245, 200, 60, 255)),
        Pair(LibraryPalette.Remove, new Rgba(240, 120, 40, 255)),
        Pair(LibraryPalette.Stale, new Rgba(150, 150, 150, 255)),
        Pair(LibraryPalette.Draft, new Rgba(200, 230, 255, 255)),
        Pair(LibraryPalette.Select, new Rgba(255, 255, 255, 255)),
    };

    public static IReadOnlyList<Rgba> OwnerRamp { get; } = new[]
    {
        new Rgba(230, 159, 0, 255),
        new Rgba(86, 180, 233, 255),
        new Rgba(0, 158, 115, 255),
        new Rgba(204, 121, 167, 255),
        new Rgba(213, 94, 0, 255),
        new Rgba(0, 114, 178, 255),
    };

    private static KeyValuePair<PaletteKey, Rgba> Pair(PaletteKey key, Rgba colour) => new(key, colour);
}

/// <summary>What motion does to timing: Reduced draws pulses, blinks and flashes steady and caps eases at 100 ms; Off also sets every ease to 0.</summary>
internal static class MotionTiming
{
    private const float ReducedCapSeconds = 0.1f;

    public static Style Apply(Style style, Motion motion)
    {
        if (motion is not (Motion.Reduced or Motion.Off))
            return style;
        return style with
        {
            Effect = Option.Some<EffectLook>(new EffectLook.Steady()),
            Enter = Option.Some<EnterLook>(new EnterLook.None()),
            Ease = Option.Some(Cap(style.Ease, motion)),
            Appear = Option.Some(Cap(style.Appear, motion)),
            Vanish = Option.Some(Cap(style.Vanish, motion)),
        };
    }

    private static Duration Cap(Option<Duration> ease, Motion motion)
    {
        if (motion == Motion.Off)
            return Duration.Zero;
        var seconds = ease.Reduce(Duration.Zero).Seconds;
        return seconds <= ReducedCapSeconds ? ease.Reduce(Duration.Zero) : Known.Value(Duration.Create(ReducedCapSeconds));
    }
}

/// <summary>Values Lattice states itself and knows to be valid.</summary>
internal static class Known
{
    public static T Value<T>(Result<T, Reason> result) where T : notnull =>
        result.TryGet(out var value) ? value : throw new InvalidOperationException("Lattice's own value was refused: " + result);
}
