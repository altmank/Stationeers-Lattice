using Lattice.Api;

namespace Lattice.Core.Styles;

/// <summary>
/// Resolves a signature's look: every active rule of its generation's program whose match holds, in cascade order;
/// per property the last that sets it wins; a property no rule sets takes Lattice's default (ghost fill, the
/// <c>planned</c> token, opacity 0.3: the game cursor's mesh alpha, <c>InventoryManager.cs:218</c>). Motion then
/// adjusts timing, and paints turn into colours under the palette in force.
/// </summary>
internal sealed class Resolver(PaletteInForce palette)
{
    private const int Properties = (int)StyleProperty.Pickable + 1;

    private static readonly StyleSource DefaultSource = new StyleSource.Default();

    /// <summary>How many resolves ran, for the invalidation tests.</summary>
    public long Count { get; private set; }

    public ResolvedLook Resolve(Signature signature)
    {
        Count++;
        var sources = new StyleSource[Properties];
        for (var i = 0; i < sources.Length; i++)
            sources[i] = DefaultSource;
        var look = Style.Empty;
        var rules = signature.Generation.Program.Rules;
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule.Active && rule.Match.Holds(signature))
                look = Over(look, rule.Style, rule.Source, sources);
        }

        var style = MotionTiming.Apply(Defaults(look), palette.Motion);
        return new ResolvedLook(style, sources, Fill(style, signature), Edge(style, signature), Glow(style, signature));
    }

    private Rgba Fill(Style style, Signature signature)
    {
        var colour = palette.Colour(style.FillPaint.Reduce(PlannedPaint), signature);
        var opacity = style.Fill.Reduce(FillMode.Ghost) == FillMode.None ? 0f : style.FillOpacity.Reduce(Fraction.One).Value;
        return Times(colour, opacity * Dim(style));
    }

    private Rgba Edge(Style style, Signature signature) =>
        style.Edge.TryGet(out var edge) && edge is EdgeLook.Line line ? Times(palette.Colour(line.Paint, signature), Dim(style)) : default;

    private Rgba Glow(Style style, Signature signature) =>
        style.Glow.TryGet(out var glow) && glow is GlowLook.Lit lit ? Times(palette.Colour(lit.Paint, signature), lit.Strength.Value * Dim(style)) : default;

    private static float Dim(Style style) => style.Dim.Reduce(Fraction.One).Value;

    private static Rgba Times(Rgba colour, float by) => colour with { A = (byte)(colour.A * by + 0.5f) };

    private static readonly Paint PlannedPaint = new Paint.Token(LibraryPalette.Planned);

    /// <summary>The look with every unset property at Lattice's default.</summary>
    private static Style Defaults(Style look)
    {
        var fill = look.Fill.Reduce(FillMode.Ghost);
        return look with
        {
            Presence = Or(look.Presence, Presence.Drawn),
            Fill = Option.Some(fill),
            FillPaint = Or(look.FillPaint, PlannedPaint),
            FillOpacity = Or(look.FillOpacity, DefaultOpacity),
            Edge = Or<EdgeLook>(look.Edge, new EdgeLook.None()),
            Pattern = Or(look.Pattern, Pattern.None),
            Glow = Or<GlowLook>(look.Glow, new GlowLook.None()),
            Effect = Or<EffectLook>(look.Effect, new EffectLook.Steady()),
            Enter = Or<EnterLook>(look.Enter, new EnterLook.None()),
            Ease = Or(look.Ease, Duration.Zero),
            Appear = Or(look.Appear, Duration.Zero),
            Vanish = Or(look.Vanish, Duration.Zero),
            SeeThrough = Or<SeeThroughLook>(look.SeeThrough, new SeeThroughLook.None()),
            BeyondClip = Or<BeyondClipLook>(look.BeyondClip, new BeyondClipLook.Drawn()),
            DistanceFade = Or(look.DistanceFade, NoFade),
            NearLimit = Or(look.NearLimit, default(Metres)),
            Dim = Or(look.Dim, Fraction.One),
            Label = Or<LabelLook>(look.Label, new LabelLook.None()),
            Icon = Or<IconLook>(look.Icon, new IconLook.None()),
            OpenEnds = Or(look.OpenEnds, ShowMode.None),
            Ports = Or(look.Ports, ShowMode.None),
            FlowArrows = Or(look.FlowArrows, ShowMode.None),
            WaitsLinks = Or(look.WaitsLinks, ShowMode.None),
            Mark = Or<MarkLook>(look.Mark, new MarkLook.None()),
            Material = Or(look.Material, MaterialOf(fill)),
            Pickable = Or(look.Pickable, Api.Pickable.Yes),
        };
    }

    private static readonly Fraction DefaultOpacity = Known.Value(Fraction.Create(0.3f));

    /// <summary>No distance fade: drawn at every distance.</summary>
    private static readonly FadeRange NoFade = Known.Value(FadeRange.Create(Known.Value(Metres.Create(float.MaxValue)), Known.Value(Metres.Create(float.MaxValue))));

    private static MaterialKey MaterialOf(FillMode fill) => fill switch
    {
        FillMode.Solid => MaterialKeys.Solid,
        FillMode.Wire => MaterialKeys.Wire,
        _ => MaterialKeys.Ghost,
    };

    private static Option<T> Or<T>(Option<T> value, T fallback) where T : notnull => value.IsSome ? value : Option.Some(fallback);

    /// <summary>The look with every property the rule's style sets taken from it, and its source noted.</summary>
    private static Style Over(Style look, Style rule, StyleSource source, StyleSource[] sources)
    {
        if (rule.Presence.IsSome) { look = look with { Presence = rule.Presence }; sources[(int)StyleProperty.Presence] = source; }
        if (rule.Fill.IsSome) { look = look with { Fill = rule.Fill }; sources[(int)StyleProperty.Fill] = source; }
        if (rule.FillPaint.IsSome) { look = look with { FillPaint = rule.FillPaint }; sources[(int)StyleProperty.FillPaint] = source; }
        if (rule.FillOpacity.IsSome) { look = look with { FillOpacity = rule.FillOpacity }; sources[(int)StyleProperty.FillOpacity] = source; }
        if (rule.Edge.IsSome) { look = look with { Edge = rule.Edge }; sources[(int)StyleProperty.Edge] = source; }
        if (rule.Pattern.IsSome) { look = look with { Pattern = rule.Pattern }; sources[(int)StyleProperty.Pattern] = source; }
        if (rule.Glow.IsSome) { look = look with { Glow = rule.Glow }; sources[(int)StyleProperty.Glow] = source; }
        if (rule.Effect.IsSome) { look = look with { Effect = rule.Effect }; sources[(int)StyleProperty.Effect] = source; }
        if (rule.Enter.IsSome) { look = look with { Enter = rule.Enter }; sources[(int)StyleProperty.Enter] = source; }
        if (rule.Ease.IsSome) { look = look with { Ease = rule.Ease }; sources[(int)StyleProperty.Ease] = source; }
        if (rule.Appear.IsSome) { look = look with { Appear = rule.Appear }; sources[(int)StyleProperty.Appear] = source; }
        if (rule.Vanish.IsSome) { look = look with { Vanish = rule.Vanish }; sources[(int)StyleProperty.Vanish] = source; }
        if (rule.SeeThrough.IsSome) { look = look with { SeeThrough = rule.SeeThrough }; sources[(int)StyleProperty.SeeThrough] = source; }
        if (rule.BeyondClip.IsSome) { look = look with { BeyondClip = rule.BeyondClip }; sources[(int)StyleProperty.BeyondClip] = source; }
        if (rule.DistanceFade.IsSome) { look = look with { DistanceFade = rule.DistanceFade }; sources[(int)StyleProperty.DistanceFade] = source; }
        if (rule.NearLimit.IsSome) { look = look with { NearLimit = rule.NearLimit }; sources[(int)StyleProperty.NearLimit] = source; }
        if (rule.Dim.IsSome) { look = look with { Dim = rule.Dim }; sources[(int)StyleProperty.Dim] = source; }
        if (rule.Label.IsSome) { look = look with { Label = rule.Label }; sources[(int)StyleProperty.Label] = source; }
        if (rule.Icon.IsSome) { look = look with { Icon = rule.Icon }; sources[(int)StyleProperty.Icon] = source; }
        if (rule.OpenEnds.IsSome) { look = look with { OpenEnds = rule.OpenEnds }; sources[(int)StyleProperty.OpenEnds] = source; }
        if (rule.Ports.IsSome) { look = look with { Ports = rule.Ports }; sources[(int)StyleProperty.Ports] = source; }
        if (rule.FlowArrows.IsSome) { look = look with { FlowArrows = rule.FlowArrows }; sources[(int)StyleProperty.FlowArrows] = source; }
        if (rule.WaitsLinks.IsSome) { look = look with { WaitsLinks = rule.WaitsLinks }; sources[(int)StyleProperty.WaitsLinks] = source; }
        if (rule.Mark.IsSome) { look = look with { Mark = rule.Mark }; sources[(int)StyleProperty.Mark] = source; }
        if (rule.Material.IsSome) { look = look with { Material = rule.Material }; sources[(int)StyleProperty.Material] = source; }
        if (rule.Pickable.IsSome) { look = look with { Pickable = rule.Pickable }; sources[(int)StyleProperty.Pickable] = source; }
        return look;
    }
}
