using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Styles;

namespace Lattice.Render.Labels;

/// <summary>
/// What a hovered finding says when the player's text style words none of it: plain words for every reason Lattice's own
/// rules put in a finding, the game's own message for a refusal that carries one, and a plain fallback for a mod's code
/// no one worded. The code itself is never shown. Lookups allocate nothing.
/// </summary>
internal static class DefaultWording
{
    /// <summary>A failing ghost's words when nothing words its reason.</summary>
    public const string CannotBuild = "Can't be built as planned";

    /// <summary>A warned ghost's words when nothing words its reason.</summary>
    public const string CheckFirst = "Check this before building";

    /// <summary>Plain words for each code Lattice's own rules put in a finding.</summary>
    public static readonly ValueMap<ReasonCode, Label> Map = WordsFor(new Dictionary<ReasonCode, string>
    {
        [ReasonCodes.NeedsSupport] = "Nothing to stand on or hang from",
        [ReasonCodes.CannotShare] = "Can't share a spot with the piece beside it",
        [ReasonCodes.EndsCollide] = "Its end touches the end of another piece",
        [ReasonCodes.FaceOverRating] = "The pressure here is higher than this piece is rated for, so it may be damaged",
        [ReasonCodes.GameRefused] = "The game won't let you build this here",
        [ReasonCodes.TargetGone] = "The piece it was planned for is gone",
        [ReasonCodes.Different] = "Something else is already built here",
        [ReasonCodes.OffSnap] = "The game doesn't build this piece at this spot on the frame",
        [ReasonCodes.TurnNotAllowed] = "The game doesn't let you build this piece turned this way",
        [ReasonCodes.NoTool] = "No tool for this step",
        [ReasonCodes.NoPiece] = "No piece fits this corner",
        [ReasonCodes.FlowBlocked] = "Items can't flow this way here",
        [ReasonCodes.Cycle] = "These pieces wait for each other, so none can be built first",
        [ReasonCodes.UnknownKind] = "Lattice doesn't understand part of this, so it can't check it",
        [ReasonCodes.UnknownPiece] = "This piece isn't available in this game",
        [ReasonCodes.RuleTimedOut] = "A check took too long and was skipped",
        [ReasonCodes.RuleSwitchedOff] = "A check failed and is switched off",
        [ReasonCodes.SwapOnAirRoom] = "Takes out a wall, window or door that holds air in",
        [ReasonCodes.CutsPower] = "Cuts power to a device keeping air, heat or life support going",
        [ReasonCodes.GasIndoors] = "A vent lets pipe gas into a room",
        [ReasonCodes.OxidiserOutside] = "Oxidiser would reach the outside",
        [ReasonCodes.HotGas] = "Opens a path to hot gas",
        [ReasonCodes.ControlsUnreachable] = "Blocks a device's controls",
        [ReasonCodes.RunThroughDoor] = "Runs through the space in front of a door",
        [ReasonCodes.OnWindow] = "Stands on or in front of a window",
        [ReasonCodes.PaintIndoors] = "Paint adds pollutant to the room's air",
        [ReasonCodes.TerrainInTheWay] = "Terrain is in the way; Lattice won't dig",
    });

    /// <summary>Words for one code when one of its facts says more than the code does. The first match wins.</summary>
    private static readonly ValueList<FactWords> ByFact = ValueList.From(new[]
    {
        new FactWords(ReasonCodes.NeedsSupport, "leaves-unsupported", Option<string>.None, "Taking this out leaves other pieces with nothing to stand on or hang from"),
        new FactWords(ReasonCodes.Different, "problem", Option.Some("broken"), "The piece built here is broken"),
        new FactWords(ReasonCodes.Different, "problem", Option.Some("turn"), "This piece is already built here, turned another way"),
        new FactWords(ReasonCodes.NoTool, "way", Option.Some("paint"), "No spray can of this colour"),
        new FactWords(ReasonCodes.NoTool, "way", Option.Some("label"), "No labeller for this step"),
    });

    /// <summary>
    /// The words a player reads for a finding's reason: the style's own words for the code, the game's message for a
    /// refusal that has one, Lattice's words for the code (as its facts refine them), else a plain fallback.
    /// </summary>
    public static string For(Reason why, TextStyle style, bool warns)
    {
        if (style.Wording.TryGetValue(why.Code, out var styled))
            return styled.Value;
        if (why.Code == ReasonCodes.GameRefused && why.GameMessage.TryGet(out var message))
            return message;
        if (FromFacts(why, out var refined))
            return refined;
        if (Map.TryGetValue(why.Code, out var words))
            return words.Value;
        return warns ? CheckFirst : CannotBuild;
    }

    private static bool FromFacts(Reason why, out string words)
    {
        for (var i = 0; i < ByFact.Count; i++)
        {
            var entry = ByFact[i];
            if (entry.Code != why.Code || !why.Facts.TryGetValue(entry.Fact, out var value))
                continue;
            if (entry.Value.TryGet(out var wanted) && wanted != value)
                continue;
            words = entry.Words;
            return true;
        }

        words = "";
        return false;
    }

    private static ValueMap<ReasonCode, Label> WordsFor(Dictionary<ReasonCode, string> words)
    {
        var labels = new Dictionary<ReasonCode, Label>(words.Count);
        foreach (var entry in words)
            labels.Add(entry.Key, Label.Create(entry.Value).Match(static label => label, static why => throw new InvalidOperationException(why.ToString())));
        return ValueMap<ReasonCode, Label>.From(labels);
    }

    private sealed record FactWords(ReasonCode Code, string Fact, Option<string> Value, string Words);
}
