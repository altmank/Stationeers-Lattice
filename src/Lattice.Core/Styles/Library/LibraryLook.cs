using System;
using Lattice.Api;

namespace Lattice.Core.Styles.Library;

/// <summary>The rows of the starter library, in cascade order inside the Library band.</summary>
internal enum LibraryRow
{
    /// <summary>A planned piece: ghost fill in the ghost's tint or <c>planned</c>, α 0.3; its edge comes from its finding.</summary>
    Planned = 1,

    /// <summary>Part built: fill α 0.45.</summary>
    Started,

    /// <summary>A structure to be taken down: no fill, edge <c>remove</c>, cross-hatch, icon ×.</summary>
    Remove,

    /// <summary>A swap's old piece: the remove look.</summary>
    SwapOld,

    /// <summary>A build or swap that carries a finish: icon ✎ (the findings and the swap icon after it still win).</summary>
    Finished,

    /// <summary>Both halves of a swap: icon ⇄ (over the planned look, or over the remove look on the old piece).</summary>
    Swap,

    /// <summary>Not checked yet: dotted <c>planned</c> edge.</summary>
    Unchecked,

    /// <summary>A draft: fill α 0.25, dashed edge <c>draft</c>; the findings looks after it still apply.</summary>
    Draft,

    /// <summary>Can be built now: solid 2 px edge <c>next</c>.</summary>
    Next,

    /// <summary>
    /// A finish-only ghost: no fill, edge in the finish's paint colour, icon ✎; over <see cref="Next"/>, under the waits,
    /// fails and warns looks.
    /// </summary>
    Finish,

    /// <summary>Waits for other ghosts: dashed edge <c>waits</c>, fill α 0.2.</summary>
    Waits,

    /// <summary>A hovered ghost that waits: links to the ghosts it waits for.</summary>
    WaitsHovered,

    /// <summary>Will fail: edge <c>fail</c>, hatch, slow pulse, icon !, three flashes when it starts failing.</summary>
    Fails,

    /// <summary>A hovered ghost that will fail: the reason as a label.</summary>
    FailsHovered,

    /// <summary>Warns: edge <c>warn</c>, dots, icon ⚠.</summary>
    Warns,

    /// <summary>Something different is there: fill <c>stale</c>, dashed <c>stale</c> edge, icon ?.</summary>
    Stale,

    /// <summary>Sent, the host has not accepted it: fill α 0.12, no edge.</summary>
    Pending,

    /// <summary>An executor works on it: glow <c>next</c>, pulse every 2 s.</summary>
    Working,

    /// <summary>An executor waits: icon ….</summary>
    Waiting,

    /// <summary>An executor paused: icon ⏸.</summary>
    Paused,

    /// <summary>Another player's ghost: dimmed to 0.6, edge in the owner's colour.</summary>
    Others,

    /// <summary>In the <c>selected</c> set: 2 px edge <c>select</c> and a glow.</summary>
    Selected,

    /// <summary>In the <c>hovered</c> set: a glow that eases in over 120 ms.</summary>
    Hovered,

    /// <summary>Leaving the board (built, removed): fades out over 400 ms.</summary>
    Done,
}

/// <summary>One row of the starter library: where <paramref name="Match"/> holds, <paramref name="Style"/>'s opinions apply.</summary>
/// <param name="Row">The row.</param>
/// <param name="Match">What it targets.</param>
/// <param name="Style">Its opinions.</param>
internal sealed record LibraryLook(LibraryRow Row, StyleMatch Match, Style Style)
{
    /// <summary>The look as a cascade rule.</summary>
    public StyleRule Rule => new(Match, Style);
}

/// <summary>The library's fixed measures, made through the contract's checks; a refusal here is a bug in the library.</summary>
internal static class LookValues
{
    /// <summary>A fraction 0 to 1.</summary>
    public static Fraction Part(float value) => Must(Fraction.Create(value));

    /// <summary>A line width in pixels.</summary>
    public static Pixels Px(float value) => Must(Pixels.Create(value));

    /// <summary>A duration in seconds.</summary>
    public static Duration Seconds(float value) => Must(Duration.Create(value));

    /// <summary>A repeat period in seconds.</summary>
    public static Period Every(float seconds) => Must(Period.Create(seconds));

    /// <summary>A repeat count.</summary>
    public static Repeats Times(int count) => Must(Repeats.Create(count));

    /// <summary>A distance in metres.</summary>
    public static Metres Distance(float value) => Must(Metres.Create(value));

    /// <summary>A palette colour token's paint.</summary>
    public static Paint Token(PaletteKey key) => new Paint.Token(key);

    /// <summary>A line edge.</summary>
    public static EdgeLook Line(LineStyle style, float width, Paint paint) => new EdgeLook.Line(style, Px(width), paint);

    /// <summary>An icon.</summary>
    public static IconLook Icon(IconKey icon) => new IconLook.Shown(icon);

    private static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static value => value, static reason => throw new InvalidOperationException(reason.ToString()));
}
