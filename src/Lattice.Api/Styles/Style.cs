namespace Lattice.Api;

/// <summary>
/// A partial look: every property is <see cref="Option{T}.None"/> ("no opinion") unless set. The look of a drawn thing
/// is, per property, the last value the cascade of enabled layers gives (<see cref="IGhostStyles"/>); a property no rule
/// sets takes Lattice's default (ghost fill, the <c>planned</c> token, opacity 0.3). To clear a value set lower down, set
/// the property's explicit <c>None</c> case. Style state is the local viewer's: it never syncs and never saves.
/// New properties arrive as new <c>init</c> properties, none by default.
/// </summary>
public sealed record Style
{
    /// <summary>A style with no opinions.</summary>
    public static Style Empty { get; } = new();

    /// <summary>Drawn or hidden. Hidden things are not drawn and not picked.</summary>
    public Option<Presence> Presence { get; init; }

    /// <summary>How the body is filled.</summary>
    public Option<FillMode> Fill { get; init; }

    /// <summary>The fill's colour.</summary>
    public Option<Paint> FillPaint { get; init; }

    /// <summary>The fill's opacity.</summary>
    public Option<Fraction> FillOpacity { get; init; }

    /// <summary>The outline.</summary>
    public Option<EdgeLook> Edge { get; init; }

    /// <summary>A pattern over the fill.</summary>
    public Option<Pattern> Pattern { get; init; }

    /// <summary>A glow.</summary>
    public Option<GlowLook> Glow { get; init; }

    /// <summary>A repeating effect.</summary>
    public Option<EffectLook> Effect { get; init; }

    /// <summary>What plays once when a thing comes to match the rule that sets this.</summary>
    public Option<EnterLook> Enter { get; init; }

    /// <summary>How long a change of look eases.</summary>
    public Option<Duration> Ease { get; init; }

    /// <summary>How long a thing (and its label) fades in when first drawn.</summary>
    public Option<Duration> Appear { get; init; }

    /// <summary>How long a thing (and its label) fades out when it leaves (done, removed, label culled).</summary>
    public Option<Duration> Vanish { get; init; }

    /// <summary>Whether it shows through walls and terrain.</summary>
    public Option<SeeThroughLook> SeeThrough { get; init; }

    /// <summary>How it looks beyond the viewer's clip.</summary>
    public Option<BeyondClipLook> BeyondClip { get; init; }

    /// <summary>A fade by distance from the camera.</summary>
    public Option<FadeRange> DistanceFade { get; init; }

    /// <summary>Not drawn nearer the camera than this.</summary>
    public Option<Metres> NearLimit { get; init; }

    /// <summary>Multiplies the final opacity (others' ghosts, traces).</summary>
    public Option<Fraction> Dim { get; init; }

    /// <summary>A label.</summary>
    public Option<LabelLook> Label { get; init; }

    /// <summary>An icon.</summary>
    public Option<IconLook> Icon { get; init; }

    /// <summary>Markers on the piece's open ends, from the catalogue's ends (modded pieces included).</summary>
    public Option<ShowMode> OpenEnds { get; init; }

    /// <summary>Port directions on a device, from the catalogue's ends.</summary>
    public Option<ShowMode> Ports { get; init; }

    /// <summary>Flow arrows (chutes, pipes), from the catalogue's ends.</summary>
    public Option<ShowMode> FlowArrows { get; init; }

    /// <summary>Links to the ghosts this ghost waits for.</summary>
    public Option<ShowMode> WaitsLinks { get; init; }

    /// <summary>A registered decoration mesh.</summary>
    public Option<MarkLook> Mark { get; init; }

    /// <summary>Which named material draws the fill (see <see cref="MaterialKeys"/>).</summary>
    public Option<MaterialKey> Material { get; init; }

    /// <summary>Whether <see cref="IGhostStyles.Pick"/> can hit it.</summary>
    public Option<Pickable> Pickable { get; init; }
}

/// <summary>The properties of a <see cref="Style"/>, as <see cref="StyleStory"/> names them. Values are fixed for the life of the contract.</summary>
public enum StyleProperty
{
    /// <summary><see cref="Style.Presence"/>.</summary>
    Presence = 1,

    /// <summary><see cref="Style.Fill"/>.</summary>
    Fill = 2,

    /// <summary><see cref="Style.FillPaint"/>.</summary>
    FillPaint = 3,

    /// <summary><see cref="Style.FillOpacity"/>.</summary>
    FillOpacity = 4,

    /// <summary><see cref="Style.Edge"/>.</summary>
    Edge = 5,

    /// <summary><see cref="Style.Pattern"/>.</summary>
    Pattern = 6,

    /// <summary><see cref="Style.Glow"/>.</summary>
    Glow = 7,

    /// <summary><see cref="Style.Effect"/>.</summary>
    Effect = 8,

    /// <summary><see cref="Style.Enter"/>.</summary>
    Enter = 9,

    /// <summary><see cref="Style.Ease"/>.</summary>
    Ease = 10,

    /// <summary><see cref="Style.Appear"/>.</summary>
    Appear = 11,

    /// <summary><see cref="Style.Vanish"/>.</summary>
    Vanish = 12,

    /// <summary><see cref="Style.SeeThrough"/>.</summary>
    SeeThrough = 13,

    /// <summary><see cref="Style.BeyondClip"/>.</summary>
    BeyondClip = 14,

    /// <summary><see cref="Style.DistanceFade"/>.</summary>
    DistanceFade = 15,

    /// <summary><see cref="Style.NearLimit"/>.</summary>
    NearLimit = 16,

    /// <summary><see cref="Style.Dim"/>.</summary>
    Dim = 17,

    /// <summary><see cref="Style.Label"/>.</summary>
    Label = 18,

    /// <summary><see cref="Style.Icon"/>.</summary>
    Icon = 19,

    /// <summary><see cref="Style.OpenEnds"/>.</summary>
    OpenEnds = 20,

    /// <summary><see cref="Style.Ports"/>.</summary>
    Ports = 21,

    /// <summary><see cref="Style.FlowArrows"/>.</summary>
    FlowArrows = 22,

    /// <summary><see cref="Style.WaitsLinks"/>.</summary>
    WaitsLinks = 23,

    /// <summary><see cref="Style.Mark"/>.</summary>
    Mark = 24,

    /// <summary><see cref="Style.Material"/>.</summary>
    Material = 25,

    /// <summary><see cref="Style.Pickable"/>.</summary>
    Pickable = 26,
}
