namespace Lattice.Api;

// The values a Style property takes. Enums are sets only Lattice extends: values are fixed for the life
// of the contract. Unions close with a private constructor and grow by cases: a case this Lattice does not
// know leaves its property unset.

/// <summary>Whether a thing is drawn. Values are fixed for the life of the contract.</summary>
public enum Presence
{
    /// <summary>Drawn.</summary>
    Drawn = 1,

    /// <summary>Not drawn and not picked.</summary>
    Hidden = 2,
}

/// <summary>How a thing's body is filled. Values are fixed for the life of the contract.</summary>
public enum FillMode
{
    /// <summary>Translucent, the game construction cursor's look.</summary>
    Ghost = 1,

    /// <summary>Opaque.</summary>
    Solid = 2,

    /// <summary>The mesh's edges only.</summary>
    Wire = 3,

    /// <summary>No fill (an edge or decorations only).</summary>
    None = 4,
}

/// <summary>A pattern over the fill: a second channel beside hue. Values are fixed for the life of the contract.</summary>
public enum Pattern
{
    /// <summary>No pattern.</summary>
    None = 1,

    /// <summary>Diagonal lines.</summary>
    Hatch = 2,

    /// <summary>Crossed diagonal lines.</summary>
    CrossHatch = 3,

    /// <summary>Dots.</summary>
    Dots = 4,
}

/// <summary>How an edge line is drawn. Values are fixed for the life of the contract.</summary>
public enum LineStyle
{
    /// <summary>Unbroken.</summary>
    Solid = 1,

    /// <summary>Dashes.</summary>
    Dashed = 2,

    /// <summary>Dots.</summary>
    Dotted = 3,
}

/// <summary>Where a label sits on its thing. Values are fixed for the life of the contract.</summary>
public enum LabelAnchor
{
    /// <summary>Above the thing's bounds.</summary>
    Above = 1,

    /// <summary>At the centre of its bounds.</summary>
    Centre = 2,

    /// <summary>Below its bounds.</summary>
    Below = 3,
}

/// <summary>Whether a decoration taken from the catalogue (open ends, ports, flow arrows, waits links) shows. Values are fixed for the life of the contract.</summary>
public enum ShowMode
{
    /// <summary>Shown.</summary>
    Shown = 1,

    /// <summary>Not shown.</summary>
    None = 2,
}

/// <summary>Whether <see cref="IGhostStyles.Pick"/> can hit a thing. Values are fixed for the life of the contract.</summary>
public enum Pickable
{
    /// <summary>Picked.</summary>
    Yes = 1,

    /// <summary>Never picked.</summary>
    No = 2,
}

/// <summary>A colour as a style names it; turned into a colour after the cascade.</summary>
public abstract record Paint
{
    private Paint() { }

    /// <summary>This colour.</summary>
    /// <param name="Colour">The colour.</param>
    public sealed record Fixed(Rgba Colour) : Paint;

    /// <summary>The palette in force's colour for a token, so a theme can recolour it.</summary>
    /// <param name="Key">The token.</param>
    public sealed record Token(PaletteKey Key) : Paint;

    /// <summary>The ghost's own <see cref="Ghost.Tint"/>, or the palette's colour for <paramref name="Fallback"/> when it has none.</summary>
    /// <param name="Fallback">The token used for a ghost with no tint, a draft or a structure.</param>
    public sealed record GhostTint(PaletteKey Fallback) : Paint;

    /// <summary>The colour of the ghost's owner, from the palette in force's owner ramp, by the owner's id.</summary>
    public sealed record OwnerColour : Paint;

    /// <summary>
    /// The colour of the paint the ghost's finish asks for, as the game shows it; the palette's colour for
    /// <paramref name="Fallback"/> when the finish asks for no paint, the thing has no finish, or the seat does not know
    /// the game's colours.
    /// </summary>
    /// <param name="Fallback">The token used when there is no finish paint to show.</param>
    public sealed record FinishColour(PaletteKey Fallback) : Paint;
}

/// <summary>An outline.</summary>
public abstract record EdgeLook
{
    private EdgeLook() { }

    /// <summary>No outline.</summary>
    public sealed record None : EdgeLook;

    /// <summary>An outline line.</summary>
    /// <param name="Style">Solid, dashed or dotted.</param>
    /// <param name="Width">Its width.</param>
    /// <param name="Paint">Its colour.</param>
    public sealed record Line(LineStyle Style, Pixels Width, Paint Paint) : EdgeLook;
}

/// <summary>A glow around a thing.</summary>
public abstract record GlowLook
{
    private GlowLook() { }

    /// <summary>No glow.</summary>
    public sealed record None : GlowLook;

    /// <summary>A glow.</summary>
    /// <param name="Paint">Its colour.</param>
    /// <param name="Strength">How strong, 0 to 1.</param>
    public sealed record Lit(Paint Paint, Fraction Strength) : GlowLook;
}

/// <summary>A repeating effect, for as long as the thing keeps the look. Motion <see cref="Motion.Reduced"/> and <see cref="Motion.Off"/> draw every effect steady.</summary>
public abstract record EffectLook
{
    private EffectLook() { }

    /// <summary>No effect.</summary>
    public sealed record Steady : EffectLook;

    /// <summary>Opacity eases down to <paramref name="Low"/> of itself and back, once a period.</summary>
    /// <param name="Period">One pulse.</param>
    /// <param name="Low">The lowest opacity, as a fraction of the thing's own.</param>
    public sealed record Pulse(Period Period, Fraction Low) : EffectLook;

    /// <summary>On for <paramref name="Duty"/> of each period, off for the rest.</summary>
    /// <param name="Period">One blink.</param>
    /// <param name="Duty">The fraction of the period it is on.</param>
    public sealed record Blink(Period Period, Fraction Duty) : EffectLook;
}

/// <summary>What plays once when a thing comes to have this value (a ghost starts failing).</summary>
public abstract record EnterLook
{
    private EnterLook() { }

    /// <summary>Nothing plays.</summary>
    public sealed record None : EnterLook;

    /// <summary>The thing flashes <paramref name="Count"/> times.</summary>
    /// <param name="Count">How many flashes.</param>
    /// <param name="Period">One flash.</param>
    public sealed record Flash(Repeats Count, Period Period) : EnterLook;
}

/// <summary>Whether a thing shows through walls and terrain in front of it.</summary>
public abstract record SeeThroughLook
{
    private SeeThroughLook() { }

    /// <summary>Hidden behind what is in front of it.</summary>
    public sealed record None : SeeThroughLook;

    /// <summary>Shows through, faintly.</summary>
    /// <param name="Strength">How strongly it shows through, 0 to 1.</param>
    /// <param name="Range">How far from the camera it shows through.</param>
    public sealed record Through(Fraction Strength, Metres Range) : SeeThroughLook;
}

/// <summary>How a thing beyond the viewer's clip (<see cref="IGhostStyles.Clip"/>) looks. Without an opinion: <see cref="Drawn"/>.</summary>
public abstract record BeyondClipLook
{
    private BeyondClipLook() { }

    /// <summary>As if there were no clip.</summary>
    public sealed record Drawn : BeyondClipLook;

    /// <summary>Not drawn.</summary>
    public sealed record Hidden : BeyondClipLook;

    /// <summary>Drawn with its opacity multiplied by <paramref name="By"/>.</summary>
    /// <param name="By">The factor.</param>
    public sealed record Dim(Fraction By) : BeyondClipLook;

    /// <summary>Drawn as its mesh's edges.</summary>
    public sealed record Wire : BeyondClipLook;
}

/// <summary>What a label says. Names that count up along a run are finish names a consumer writes into the ghosts; the label only shows them.</summary>
public abstract record LabelText
{
    private LabelText() { }

    /// <summary>The ghost's <see cref="Ghost.Label"/>.</summary>
    public sealed record GhostLabel : LabelText;

    /// <summary>The name the ghost's finish gives the structure.</summary>
    public sealed record FinishName : LabelText;

    /// <summary>The reason of the ghost's finding, worded by the text style's wording table (its code when the table has none).</summary>
    public sealed record FindingReason : LabelText;

    /// <summary>The name of the ghost's plan.</summary>
    public sealed record PlanTitle : LabelText;

    /// <summary>The ghost's position in its plan's validation report, which is in build order.</summary>
    public sealed record StepNumber : LabelText;

    /// <summary>The count of the ghost's piece in its plan's bill.</summary>
    public sealed record BillCount : LabelText;

    /// <summary>Fixed words.</summary>
    /// <param name="Words">The words.</param>
    public sealed record Literal(Label Words) : LabelText;
}

/// <summary>A label in the world, on a drawn thing.</summary>
public abstract record LabelLook
{
    private LabelLook() { }

    /// <summary>No label.</summary>
    public sealed record None : LabelLook;

    /// <summary>A label. Lattice shows the nearest in range first, by priority, culls overlaps and caps how many show.</summary>
    /// <param name="Says">What it says.</param>
    /// <param name="Style">The registered text style it is drawn in.</param>
    /// <param name="Anchor">Where it sits.</param>
    /// <param name="Priority">Higher shows first when labels compete.</param>
    /// <param name="Range">How far from the camera it shows.</param>
    public sealed record Shown(LabelText Says, TextStyleKey Style, LabelAnchor Anchor, int Priority, Metres Range) : LabelLook;
}

/// <summary>An icon on a drawn thing.</summary>
public abstract record IconLook
{
    private IconLook() { }

    /// <summary>No icon.</summary>
    public sealed record None : IconLook;

    /// <summary>A registered icon, or one of Lattice's glyph icons.</summary>
    /// <param name="Icon">The icon.</param>
    public sealed record Shown(IconKey Icon) : IconLook;
}

/// <summary>A registered decoration mesh on a drawn thing.</summary>
public abstract record MarkLook
{
    private MarkLook() { }

    /// <summary>No decoration.</summary>
    public sealed record None : MarkLook;

    /// <summary>A decoration registered under <paramref name="Mark"/>.</summary>
    /// <param name="Mark">The decoration.</param>
    public sealed record Shown(DecorationKey Mark) : MarkLook;
}
