using System;
using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Catalogue;

/// <summary>
/// The words of the catalogue data file (<see cref="CatalogueDataFile.Header"/>) for directions, network kinds, end
/// roles, slots, pipe contents, kit kinds and display names, both ways, so the file Lattice writes from the game and the
/// file the test kit reads agree.
/// </summary>
internal static class CatalogueText
{
    public static string Direction(Direction direction) => direction switch
    {
        Ports.Direction.XPlus => "x+",
        Ports.Direction.XMinus => "x-",
        Ports.Direction.YPlus => "y+",
        Ports.Direction.YMinus => "y-",
        Ports.Direction.ZPlus => "z+",
        Ports.Direction.ZMinus => "z-",
        _ => "?",
    };

    public static Direction? ParseDirection(string text) => text switch
    {
        "x+" => Ports.Direction.XPlus,
        "x-" => Ports.Direction.XMinus,
        "y+" => Ports.Direction.YPlus,
        "y-" => Ports.Direction.YMinus,
        "z+" => Ports.Direction.ZPlus,
        "z-" => Ports.Direction.ZMinus,
        _ => null,
    };

    /// <summary>Comma-separated, in the game's bit order; a modded kind is <c>mod:&lt;GameTypeName&gt;:&lt;bits&gt;</c>.</summary>
    public static string Kinds(ValueSet<NetworkType> kinds)
    {
        var words = new List<string>(kinds.Count);
        foreach (var known in KnownKinds)
            if (kinds.Contains(known.Type))
                words.Add(known.Word);
        foreach (var kind in kinds)
            if (kind is NetworkType.Unknown unknown)
                words.Add("mod:" + unknown.GameTypeName + ":" + unknown.Bits.ToString(CultureInfo.InvariantCulture));
        return string.Join(",", words);
    }

    public static Option<ValueSet<NetworkType>> ParseKinds(string text)
    {
        var kinds = new List<NetworkType>(2);
        foreach (var word in text.Split(','))
        {
            if (word.StartsWith("mod:", StringComparison.Ordinal))
            {
                var colon = word.LastIndexOf(':');
                if (colon <= 4 || !int.TryParse(word[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits))
                    return Option<ValueSet<NetworkType>>.None;
                kinds.Add(new NetworkType.Unknown(word[4..colon], bits));
                continue;
            }

            var found = false;
            foreach (var known in KnownKinds)
                if (known.Word == word)
                {
                    kinds.Add(known.Type);
                    found = true;
                }

            if (!found)
                return Option<ValueSet<NetworkType>>.None;
        }

        return kinds.Count == 0 ? Option<ValueSet<NetworkType>>.None : Option.Some(ValueSet.From(kinds));
    }

    public static string Role(ConnectionRole role) => role switch
    {
        ConnectionRole.Input => "input",
        ConnectionRole.Input2 => "input2",
        ConnectionRole.Output => "output",
        ConnectionRole.Output2 => "output2",
        ConnectionRole.Waste => "waste",
        _ => "?",
    };

    public static ConnectionRole? ParseRole(string text) => text switch
    {
        "input" => ConnectionRole.Input,
        "input2" => ConnectionRole.Input2,
        "output" => ConnectionRole.Output,
        "output2" => ConnectionRole.Output2,
        "waste" => ConnectionRole.Waste,
        _ => null,
    };

    public static string Slot(Slot slot) => slot switch
    {
        Ports.Slot.Centre => "centre",
        Ports.Slot.FaceXPlus => "face-x+",
        Ports.Slot.FaceXMinus => "face-x-",
        Ports.Slot.FaceYPlus => "face-y+",
        Ports.Slot.FaceYMinus => "face-y-",
        Ports.Slot.FaceZPlus => "face-z+",
        Ports.Slot.FaceZMinus => "face-z-",
        Ports.Slot.Cable => "cable",
        Ports.Slot.Pipe => "pipe",
        Ports.Slot.Chute => "chute",
        Ports.Slot.Device => "device",
        Ports.Slot.Rail => "rail",
        Ports.Slot.Other => "other",
        _ => "?",
    };

    public static Slot? ParseSlot(string text)
    {
        for (var slot = Ports.Slot.Centre; slot <= Ports.Slot.Other; slot++)
            if (Slot(slot) == text)
                return slot;
        return null;
    }

    public static string Pipe(PipeContent content) => content switch
    {
        PipeContent.Gas => "gas",
        PipeContent.Liquid => "liquid",
        PipeContent.Any => "any",
        _ => "?",
    };

    /// <summary>The keeps word: <c>air</c>, <c>heat</c>, <c>life</c>, comma separated.</summary>
    public static string Keeps(Keeps keeps)
    {
        var words = new System.Collections.Generic.List<string>(3);
        if ((keeps & Ports.Keeps.Air) != 0)
            words.Add("air");
        if ((keeps & Ports.Keeps.Heat) != 0)
            words.Add("heat");
        if ((keeps & Ports.Keeps.Life) != 0)
            words.Add("life");
        return string.Join(",", words);
    }

    /// <summary>Reads a keeps word; null for an unknown word or an empty list.</summary>
    public static Keeps? ParseKeeps(string text)
    {
        var keeps = Ports.Keeps.None;
        foreach (var word in text.Split(','))
        {
            var one = word switch
            {
                "air" => Ports.Keeps.Air,
                "heat" => Ports.Keeps.Heat,
                "life" => Ports.Keeps.Life,
                _ => Ports.Keeps.None,
            };
            if (one == Ports.Keeps.None)
                return null;
            keeps |= one;
        }

        return keeps;
    }

    public static string Opening(OpeningKind kind) => kind switch
    {
        OpeningKind.Door => "door",
        OpeningKind.Window => "window",
        _ => "?",
    };

    public static OpeningKind? ParseOpening(string text) => text switch
    {
        "door" => OpeningKind.Door,
        "window" => OpeningKind.Window,
        _ => null,
    };

    public static string Placement(FacePlacement kind) => kind switch
    {
        FacePlacement.Face => "face",
        FacePlacement.FaceMount => "face-mount",
        _ => "?",
    };

    public static FacePlacement? ParsePlacement(string text) => text switch
    {
        "face" => FacePlacement.Face,
        "face-mount" => FacePlacement.FaceMount,
        _ => null,
    };

    /// <summary>A snap as the file writes it: the sizes on x, y and z, a slash, then the offsets, in metres (<c>2,0.5,2/0,0.25,0</c>).</summary>
    public static string Snap(PieceSnap snap) =>
        Metres(snap.X.Size) + "," + Metres(snap.Y.Size) + "," + Metres(snap.Z.Size) + "/" +
        Metres(snap.X.Offset) + "," + Metres(snap.Y.Offset) + "," + Metres(snap.Z.Offset);

    /// <summary>A snap read back from <see cref="Snap(PieceSnap)"/>'s text; none when it does not read.</summary>
    public static Option<PieceSnap> ParseSnap(string text)
    {
        var halves = text.Split('/');
        if (halves.Length != 2)
            return Option<PieceSnap>.None;
        var sizes = halves[0].Split(',');
        var offsets = halves[1].Split(',');
        if (sizes.Length != 3 || offsets.Length != 3)
            return Option<PieceSnap>.None;
        var values = new float[6];
        for (var i = 0; i < 3; i++)
            if (!float.TryParse(sizes[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                !float.TryParse(offsets[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i + 3]))
                return Option<PieceSnap>.None;
        return Option.Some(new PieceSnap(
            new AxisSnap(values[0], values[3]), new AxisSnap(values[1], values[4]), new AxisSnap(values[2], values[5])));
    }

    private static string Metres(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static PipeContent? ParsePipe(string text) => text switch
    {
        "gas" => PipeContent.Gas,
        "liquid" => PipeContent.Liquid,
        "any" => PipeContent.Any,
        _ => null,
    };

    /// <summary>A piece family as written: the key itself (<c>cable</c>, <c>owner/name</c>).</summary>
    public static Option<PieceFamily> ParseFamily(string text) =>
        PieceFamily.Parse(text).TryGet(out var family) ? Option.Some(family) : Option<PieceFamily>.None;

    /// <summary>
    /// The words of a look line after <c>look</c>: the bounds box (centre x y z, size x y z, metres), then the source:
    /// <c>meshes &lt;count&gt; &lt;readable&gt; &lt;by-step&gt;</c>, <c>blueprint readable|unreadable</c>, <c>box</c> or <c>cell</c>.
    /// </summary>
    public static string Look(LookFacts look)
    {
        var box = look.Bounds;
        var bounds = Metres(box.Centre.X) + " " + Metres(box.Centre.Y) + " " + Metres(box.Centre.Z) + " " +
                     Metres(box.Size.X) + " " + Metres(box.Size.Y) + " " + Metres(box.Size.Z);
        return bounds + " " + look.Source switch
        {
            LookSource.Meshes m => "meshes " + Count(m.Count) + " " + Count(m.Readable) + " " + Count(m.ByStep),
            LookSource.Blueprint b => b.Readable ? "blueprint readable" : "blueprint unreadable",
            LookSource.Box => "box",
            _ => "cell",
        };
    }

    /// <summary>Reads the words after <c>look</c> (<see cref="Look"/>); none when they are not a look.</summary>
    public static Option<LookFacts> ParseLook(IReadOnlyList<string> words, int start)
    {
        if (words.Count < start + 7)
            return Option<LookFacts>.None;
        var numbers = new double[6];
        for (var i = 0; i < 6; i++)
            if (!double.TryParse(words[start + i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || (i >= 3 && numbers[i] < 0))
                return Option<LookFacts>.None;
        var bounds = new PieceBox(new Vector(numbers[0], numbers[1], numbers[2]), new Vector(numbers[3], numbers[4], numbers[5]));
        var rest = words.Count - start - 6;
        LookSource? source = words[start + 6] switch
        {
            "meshes" when rest == 4 && Counted(words[start + 7]) is { } count && Counted(words[start + 8]) is { } readable &&
                          Counted(words[start + 9]) is { } byStep && readable <= count && byStep <= count
                => new LookSource.Meshes(count, readable, byStep),
            "blueprint" when rest == 2 && words[start + 7] is "readable" or "unreadable"
                => new LookSource.Blueprint(words[start + 7] == "readable"),
            "box" when rest == 1 => new LookSource.Box(),
            "cell" when rest == 1 => new LookSource.Cell(),
            _ => null,
        };
        return source is null ? Option<LookFacts>.None : Option.Some(new LookFacts(bounds, source));
    }

    private static string Metres(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int? Counted(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static (NetworkType Type, string Word)[] KnownKinds =>
    [
        (new NetworkType.GasPipe(), "gas"),
        (new NetworkType.Power(), "power"),
        (new NetworkType.Data(), "data"),
        (new NetworkType.Chute(), "chute"),
        (new NetworkType.Elevator(), "elevator"),
        (new NetworkType.LiquidPipe(), "liquid"),
        (new NetworkType.LandingPad(), "landing-pad"),
        (new NetworkType.LaunchPad(), "launch-pad"),
        (new NetworkType.Rail(), "rail"),
    ];

    /// <summary>The file's word for a kit kind.</summary>
    public static string Kind(KitKind kind) => kind switch
    {
        KitKind.Single => "single",
        KitKind.Merge => "merge",
        _ => "multi",
    };

    public static KitKind? ParseKind(string text) => text switch
    {
        "single" => KitKind.Single,
        "multi" => KitKind.Multi,
        "merge" => KitKind.Merge,
        _ => null,
    };

    /// <summary>
    /// A display name as one word of the file: '%', '#', ',', white space and control characters written as
    /// <c>%XX</c> (<c>%uXXXX</c> above 0xFF), everything else as it is.
    /// </summary>
    public static string Name(string text)
    {
        var escaped = new System.Text.StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '%' or '#' or ',' || char.IsWhiteSpace(c) || char.IsControl(c))
                escaped.Append(c <= 0xFF ? "%" + ((int)c).ToString("X2", CultureInfo.InvariantCulture) : "%u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else
                escaped.Append(c);
        }

        return escaped.ToString();
    }

    /// <summary>A display name read back from its word; none for a malformed escape.</summary>
    public static Option<string> ParseName(string word)
    {
        var text = new System.Text.StringBuilder(word.Length);
        for (var i = 0; i < word.Length; i++)
        {
            if (word[i] != '%')
            {
                text.Append(word[i]);
                continue;
            }

            var wide = i + 1 < word.Length && word[i + 1] == 'u';
            var start = wide ? i + 2 : i + 1;
            var digits = wide ? 4 : 2;
            if (start + digits > word.Length ||
                !int.TryParse(word.Substring(start, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                return Option<string>.None;
            text.Append((char)code);
            i = start + digits - 1;
        }

        return Option.Some(text.ToString());
    }
}
