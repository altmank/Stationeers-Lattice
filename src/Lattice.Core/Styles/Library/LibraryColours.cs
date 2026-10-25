using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Styles.Library;

/// <summary>
/// The library palettes' colours. Opacity lives in the styles (fill opacity, dim), so every colour here is opaque.
/// The game's own colours: Unity's <c>Color.blue</c> for blueprints (Assets.Scripts.UI/Wireframe.cs:28), and
/// <c>Color.green</c> placeable, <c>Color.red</c> refused, <c>Color.yellow</c> joins a network for the construction
/// cursor (Assets.Scripts.Inventory/InventoryManager.cs:2900-2932).
/// </summary>
internal static class LibraryColours
{
    /// <summary>Unity's <c>Color.blue</c>.</summary>
    public static Rgba GameBlue => GameColours.Blue;

    /// <summary>Unity's <c>Color.green</c>.</summary>
    public static Rgba GameGreen => GameColours.Green;

    /// <summary>Unity's <c>Color.red</c>.</summary>
    public static Rgba GameRed => GameColours.Red;

    /// <summary>Unity's <c>Color.yellow</c> (1, 0.92, 0.016).</summary>
    public static Rgba GameYellow => GameColours.Yellow;

    /// <summary>The game cursor's colours, with Lattice's own for the states the game has no colour for.</summary>
    public static Palette Default() => Make(LibraryPalettes.Default, new Colours(
        Planned: GameBlue,
        Next: GameGreen,
        Waits: Rgb(255, 140, 0),
        Fail: GameRed,
        Warn: GameYellow,
        Remove: Rgb(230, 60, 230),
        Stale: Rgb(128, 128, 128),
        Draft: Rgb(128, 170, 255),
        Select: Rgb(255, 255, 255)),
        OkabeIto());

    /// <summary>Okabe-Ito hues (Okabe and Ito, "Color Universal Design", 2008), told apart under the common colour-vision differences.</summary>
    public static Palette ColourSafe() => Make(LibraryPalettes.ColourSafe, new Colours(
        Planned: Rgb(86, 180, 233),
        Next: Rgb(0, 158, 115),
        Waits: Rgb(230, 159, 0),
        Fail: Rgb(213, 94, 0),
        Warn: Rgb(240, 228, 66),
        Remove: Rgb(204, 121, 167),
        Stale: Rgb(153, 153, 153),
        Draft: Rgb(0, 114, 178),
        Select: Rgb(255, 255, 255)),
        OkabeIto());

    /// <summary>Saturated colours, light states against the dark ones.</summary>
    public static Palette HighContrast() => Make(LibraryPalettes.HighContrast, new Colours(
        Planned: Rgb(0, 255, 255),
        Next: Rgb(0, 255, 0),
        Waits: Rgb(255, 128, 0),
        Fail: Rgb(255, 0, 0),
        Warn: Rgb(255, 255, 0),
        Remove: Rgb(255, 0, 255),
        Stale: Rgb(160, 160, 160),
        Draft: Rgb(128, 128, 255),
        Select: Rgb(255, 255, 255)),
        new[]
        {
            Rgb(255, 255, 0), Rgb(0, 255, 255), Rgb(255, 0, 255), Rgb(0, 255, 0),
            Rgb(255, 128, 0), Rgb(255, 255, 255), Rgb(128, 128, 255), Rgb(255, 0, 0),
        });

    private static Rgba[] OkabeIto() => new[]
    {
        Rgb(230, 159, 0), Rgb(86, 180, 233), Rgb(0, 158, 115), Rgb(240, 228, 66),
        Rgb(0, 114, 178), Rgb(213, 94, 0), Rgb(204, 121, 167), Rgb(255, 255, 255),
    };

    private static Palette Make(PaletteId id, Colours c, Rgba[] ramp)
    {
        var colours = new Dictionary<PaletteKey, Rgba>(9)
        {
            [LibraryPalette.Planned] = c.Planned,
            [LibraryPalette.Next] = c.Next,
            [LibraryPalette.Waits] = c.Waits,
            [LibraryPalette.Fail] = c.Fail,
            [LibraryPalette.Warn] = c.Warn,
            [LibraryPalette.Remove] = c.Remove,
            [LibraryPalette.Stale] = c.Stale,
            [LibraryPalette.Draft] = c.Draft,
            [LibraryPalette.Select] = c.Select,
        };
        return new Palette(id, ValueMap.From(colours)) { OwnerRamp = ValueList.From(ramp) };
    }

    private static Rgba Rgb(byte r, byte g, byte b) => new(r, g, b, 255);

    private readonly record struct Colours(
        Rgba Planned, Rgba Next, Rgba Waits, Rgba Fail, Rgba Warn, Rgba Remove, Rgba Stale, Rgba Draft, Rgba Select);
}
