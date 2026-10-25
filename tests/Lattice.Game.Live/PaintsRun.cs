using System;
using System.Collections;
using Assets.Scripts;
using Assets.Scripts.Util;
using Lattice.Api;
using Lattice.Game.World;
using UnityEngine;

namespace Lattice.Game.Live;

/// <summary>
/// The game's paint colours read back in a running world (mode <c>paints</c>): every <c>GameManager.CustomColors</c>
/// swatch's name, its <c>Color</c> and its <c>Normal</c> material's colour, and what <see cref="GamePaintColours"/> answers.
/// </summary>
internal static class PaintsRun
{
    public static IEnumerator Run(Action<string> line)
    {
        yield return new WaitForSecondsRealtime(2f);
        var manager = Singleton<GameManager>.Instance;
        if (manager == null || manager.CustomColors == null)
        {
            line("FAIL paints: no game manager colours");
            yield break;
        }

        if (!GamePaintColours.Read().TryGet(out var paints))
        {
            line("FAIL paints: GamePaintColours read nothing");
            yield break;
        }

        int white = 0, answered = 0, differs = 0;
        for (var i = 0; i < manager.CustomColors.Count; i++)
        {
            var swatch = manager.CustomColors[i];
            var material = swatch != null && swatch.Normal != null && swatch.Normal.HasProperty("_Color") ? swatch.Normal.color : Color.clear;
            var colour = swatch != null ? swatch.Color : Color.clear;
            var read = PaintColour.Create(i).TryGet(out var paint) ? paints.Colour(paint) : Option<Rgba>.None;
            white += colour == Color.white ? 1 : 0;
            answered += read.IsSome ? 1 : 0;
            differs += material != Color.clear && Vector4.Distance(material, colour) > 0.02f ? 1 : 0;
            line($"paints {i} {(swatch != null ? swatch.Name : "null")}: Color {colour}, Normal {material}, read {read}");
        }

        line($"paints counts: {manager.CustomColors.Count} swatches, {answered} answered, {white} white, {differs} differ from their Normal material");
        line($"{(answered == manager.CustomColors.Count && white < manager.CustomColors.Count / 2 ? "PASS" : "FAIL")} the game's paint colours are read and hold real colours");
    }
}
