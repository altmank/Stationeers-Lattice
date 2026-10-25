using System;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Util;
using Lattice.Api;
using Lattice.Core.Styles;
using UnityEngine;

namespace Lattice.Game.World;

/// <summary>
/// The game's paint colours: <c>GameManager.CustomColors</c>, a list of <c>ColorSwatch</c> indexed by the paint index a
/// structure saves (decompile Assets.Scripts/GameManager.cs:123, GetColorIndex at 534; Assets.Scripts.Objects/ColorSwatch.cs
/// <c>Color</c>). Read once when the world starts, on the main thread; a lookup is an array read and allocates nothing.
/// </summary>
internal sealed class GamePaintColours : IPaintColours
{
    private readonly Rgba[] _colours;

    private GamePaintColours(Rgba[] colours) => _colours = colours;

    /// <summary>How many paints the game holds.</summary>
    public int Count => _colours.Length;

    /// <summary>The game's colours now; none when the game manager or its colour list is not there yet.</summary>
    public static Option<GamePaintColours> Read()
    {
        var manager = Singleton<GameManager>.Instance;
        if (manager == null || manager.CustomColors == null)
            return Option<GamePaintColours>.None;
        var swatches = manager.CustomColors;
        var colours = new Rgba[swatches.Count];
        for (var i = 0; i < swatches.Count; i++)
            colours[i] = swatches[i] == null ? default : Of(swatches[i].Color);
        return Option.Some(new GamePaintColours(colours));
    }

    /// <inheritdoc />
    public Option<Rgba> Colour(PaintColour paint) =>
        paint.Index >= 0 && paint.Index < _colours.Length && _colours[paint.Index].A > 0
            ? Option.Some(_colours[paint.Index])
            : Option<Rgba>.None;

    private static Rgba Of(Color colour) => new(Byte(colour.r), Byte(colour.g), Byte(colour.b), Byte(colour.a));

    private static byte Byte(float channel) => (byte)Math.Round(Math.Max(0f, Math.Min(1f, channel)) * 255f);
}
