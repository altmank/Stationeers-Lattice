using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;

namespace Lattice.TestKit.Catalogue;

/// <summary>
/// The catalogue data file: a line-based text file of pieces, the format the Catalogue reader package writes and the
/// test kit loads. Blank lines and text after '#' are ignored. Grammar:
/// <code>
/// lattice-catalogue 3
/// broken-tools &lt;Item&gt;,...                                              the tools that take a broken structure apart; at most once
/// kit &lt;KitName&gt; kind=single|multi|merge [name=&lt;text&gt;] options=&lt;PrefabName&gt;,...   every option in scroll order; at most once per kit
/// item &lt;ItemName&gt; [name=&lt;text&gt;] [paint=&lt;colour&gt; acts=&lt;n&gt;] [labeller=yes]   an item prefab (kit, tool, ingot, ...) and its display name; a spray can with its colour index and acts per full can; a labeller; at most once per item
/// piece &lt;PrefabName&gt; [kit=&lt;KitName&gt; option=&lt;n&gt; [also=&lt;KitName&gt;:&lt;n&gt;,...]] grid=small|large [name=&lt;text&gt;] [support=&lt;group&gt;] [pipe=gas|liquid|any] [opening=door|window] [merge=&lt;Item&gt;] [keeps=air,heat,life] [supplies=yes] [control=&lt;direction&gt;] [vents=yes] [family=&lt;key&gt;] [place=face|face-mount] [snap=&lt;sx&gt;,&lt;sy&gt;,&lt;sz&gt;/&lt;ox&gt;,&lt;oy&gt;,&lt;oz&gt;] [turning=grid|face|face-mount/&lt;axes xyz|none&gt;/&lt;faces wcf|none&gt;[/mounted]]
///   step [entry=&lt;Item&gt;] [material=&lt;Item&gt;*&lt;n&gt;] [exit=&lt;Item&gt;] air=yes|no [gravity=yes|no]   one per build step, in order
///   end &lt;x&gt; &lt;y&gt; &lt;z&gt; x+|x-|y+|y-|z+|z- &lt;kinds&gt; [reach=&lt;mm&gt;] [role=input|input2|output|output2|waste] [index=&lt;n&gt;] [game=&lt;text&gt;]   index: in the game's OpenEnds; game: its ConnectionType name
///   cell &lt;x&gt; &lt;y&gt; &lt;z&gt; centre|face-x+|..|face-z-|cable|pipe|chute|device|rail|other  a footprint cell
///   collision &lt;n&gt;                                                       small-grid collision class bits
///   block &lt;x&gt; &lt;y&gt; &lt;z&gt;                                                 a face piece's blocking point, decimetres from its position, unturned
///   centre &lt;x&gt; &lt;y&gt; &lt;z&gt;                                                a face piece's bounds centre, metres from its position, unturned; at most once
///   rating [cable=&lt;W&gt;] [pipe=&lt;kPa&gt;] [face=&lt;kPa&gt;]
///   look &lt;cx&gt; &lt;cy&gt; &lt;cz&gt; &lt;sx&gt; &lt;sy&gt; &lt;sz&gt; meshes &lt;n&gt; &lt;readable&gt; &lt;by-step&gt; | blueprint readable|unreadable | box | cell   at most once
/// </code>
/// A piece with no <c>kit=</c> is one no kit places; <c>also=</c> lists its other kits. A <c>kit</c> line must agree with
/// every piece line that names its kit; a kit without one takes its options from the piece lines (a file may hold part
/// of a kit, to join another file's). A kit line may name a piece the file has no line for (one the game's reader
/// refused). <c>name=</c> is
/// the display name, escaped (<see cref="CatalogueText.Name"/>); without it the prefab name stands in. Kinds are comma-separated:
/// gas, power, data, chute, elevator, liquid, landing-pad, launch-pad, rail, or <c>mod:&lt;GameTypeName&gt;:&lt;bits&gt;</c>
/// (<see cref="CatalogueText"/>). A support group is one of Lattice's (<c>lattice.support.frame-below</c>) or a mod's
/// (<c>owner/name</c>). Names are prefab names; keys are their prefab hashes (<see cref="PrefabHash"/>). An end gives
/// the offset in decimetres of the cell it leaves the piece from (relative to the piece's own cell, unturned) and its
/// outward direction; <c>place=</c> marks a piece the game puts on a face, whose <c>block</c> and <c>centre</c> lines
/// say which cells and slots it takes (<see cref="FaceFacts"/>; no <c>centre</c> line is the origin); two pieces connect when each has an end sharing a kind in its own cell pointing at the other's
/// (connection is geometric). An end with a role is a device's port.
/// </summary>
internal static class CatalogueFile
{
    public const string Header = CatalogueDataFile.Header;

    public static Result<FakeCatalogue, FileError> Read(TextReader reader, string fileName)
    {
        var pieces = new List<NamedPiece>();
        var kitLines = new List<KitLine>();
        var itemLines = new List<ItemLine>();
        var itemNames = new HashSet<string>(StringComparer.Ordinal);
        string[]? brokenTools = null;
        Draft? draft = null;
        var lineNumber = 0;
        var sawHeader = false;
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            var hash = raw.IndexOf('#');
            var words = (hash >= 0 ? raw[..hash] : raw).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                continue;
            if (!sawHeader)
            {
                if (words[0] != CatalogueDataFile.Magic || words.Length != 2)
                    return Fail(fileName, lineNumber, $"expected '{Header}'");
                if (words[1] != CatalogueDataFile.Format.ToString(CultureInfo.InvariantCulture))
                    return Fail(fileName, lineNumber,
                        $"format {words[1]}, this reader reads format {CatalogueDataFile.Format}: write the file again " +
                        $"(the game with -lattice-write-catalogue, then make-fixtures.ps1 -Catalogue)");
                sawHeader = true;
                continue;
            }

            var problem = words[0] switch
            {
                "piece" => StartPiece(words, ref draft, pieces),
                "kit" => AddKit(words, kitLines),
                "item" => AddItem(words, itemLines, itemNames),
                "broken-tools" => brokenTools is not null ? "broken-tools given twice"
                    : words.Length != 2 ? "broken-tools needs one comma-separated list of items"
                    : SetBrokenTools(words[1], out brokenTools),
                "step" => draft is null ? "step before any piece" : draft.AddStep(words),
                "end" => draft is null ? "end before any piece" : draft.AddEnd(words),
                "rating" => draft is null ? "rating before any piece" : draft.SetRating(words),
                "cell" => draft is null ? "cell before any piece" : draft.AddCell(words),
                "collision" => draft is null ? "collision before any piece" : draft.SetCollision(words),
                "block" => draft is null ? "block before any piece" : draft.AddBlock(words),
                "centre" => draft is null ? "centre before any piece" : draft.SetCentre(words),
                "look" => draft is null ? "look before any piece" : draft.SetLook(words),
                _ => $"unknown line kind '{words[0]}'",
            };
            if (problem is not null)
                return Fail(fileName, lineNumber, problem);
        }

        if (!sawHeader)
            return Fail(fileName, 0, "empty file");
        if (draft is not null)
        {
            var problem = draft.Finish(pieces);
            if (problem is not null)
                return Fail(fileName, lineNumber, problem);
        }

        var seen = new HashSet<KitPlace>();
        foreach (var piece in pieces)
            for (var i = 0; i < piece.Facts.Kits.Count; i++)
                if (!seen.Add(piece.Facts.Kits[i]))
                    return Fail(fileName, 0, $"{piece.Name}: option {piece.Facts.Kits[i].Option.Index} of {piece.KitNames[i]} given twice");
        if (KitsAgree(pieces, kitLines) is { } disagreement)
            return Fail(fileName, 0, disagreement);
        return Result<FakeCatalogue, FileError>.Ok(new FakeCatalogue(pieces, brokenTools, kitLines, itemLines));
    }

    private static string? AddItem(string[] words, List<ItemLine> itemLines, HashSet<string> seen)
    {
        if (words.Length < 2)
            return "item needs a name";
        var fields = Fields(words, 2);
        if (fields is null)
            return "expected key=value fields";
        foreach (var key in fields.Keys)
            if (key is not ("name" or "paint" or "acts" or "labeller"))
                return $"unknown item field '{key}'";
        if (!seen.Add(words[1]))
            return $"item {words[1]} given twice";
        var displayName = words[1];
        if (fields.TryGetValue("name", out var nameText) && !CatalogueText.ParseName(nameText).TryGet(out displayName))
            return $"bad name '{nameText}'";
        var paint = Option<(int, int)>.None;
        if (fields.TryGetValue("paint", out var colourText) || fields.ContainsKey("acts"))
        {
            if (colourText is null || !int.TryParse(colourText, NumberStyles.None, CultureInfo.InvariantCulture, out var colour))
                return "a spray can needs paint=<colour index>";
            if (!fields.TryGetValue("acts", out var actsText) || !int.TryParse(actsText, NumberStyles.None, CultureInfo.InvariantCulture, out var acts))
                return "a spray can needs acts=<acts per can>";
            paint = Lattice.Api.Option.Some((colour, acts));
        }

        var labeller = false;
        if (fields.TryGetValue("labeller", out var labellerText))
        {
            if (labellerText != "yes")
                return "labeller takes yes";
            labeller = true;
        }

        itemLines.Add(new ItemLine(words[1], displayName) { Paint = paint, Labeller = labeller });
        return null;
    }

    private static string? AddKit(string[] words, List<KitLine> kitLines)
    {
        if (words.Length < 2)
            return "kit needs a name";
        var fields = Fields(words, 2);
        if (fields is null)
            return "expected key=value fields";
        foreach (var held in kitLines)
            if (held.Name == words[1])
                return $"kit {words[1]} given twice";
        if (!fields.TryGetValue("kind", out var kindText) || CatalogueText.ParseKind(kindText) is not { } kind)
            return "kit needs kind=single|multi|merge";
        if (!fields.TryGetValue("options", out var optionsText))
            return "kit needs options=";
        var displayName = words[1];
        if (fields.TryGetValue("name", out var nameText) && !CatalogueText.ParseName(nameText).TryGet(out displayName))
            return $"bad name '{nameText}'";
        var options = optionsText.Length == 0 ? [] : optionsText.Split(',');
        foreach (var option in options)
            if (option.Length == 0)
                return "empty piece in options=";
        kitLines.Add(new KitLine(words[1], kind, displayName, ValueList.From(options)));
        return null;
    }

    // A kit line holds every option, so every piece line naming its kit must agree with it.
    private static string? KitsAgree(List<NamedPiece> pieces, List<KitLine> kitLines)
    {
        var lines = new Dictionary<string, KitLine>(kitLines.Count, StringComparer.Ordinal);
        foreach (var line in kitLines)
            lines.Add(line.Name, line);
        foreach (var piece in pieces)
            for (var i = 0; i < piece.KitNames.Count; i++)
            {
                var index = piece.Facts.Kits[i].Option.Index;
                if (lines.TryGetValue(piece.KitNames[i], out var line) && (index >= line.Options.Count || line.Options[index] != piece.Name))
                    return $"{piece.Name}: option {index} of {piece.KitNames[i]} is {(index < line.Options.Count ? line.Options[index] : "past its last")} in its kit line";
            }

        return null;
    }

    private static string? SetBrokenTools(string list, out string[] tools)
    {
        tools = list.Split(',');
        foreach (var tool in tools)
            if (tool.Length == 0)
                return "empty item in broken-tools";
        return null;
    }

    public static void Write(FakeCatalogue catalogue, TextWriter writer)
    {
        writer.WriteLine(Header);
        if (catalogue.BrokenToolNames.Count > 0)
            writer.WriteLine($"broken-tools {string.Join(",", catalogue.BrokenToolNames)}");
        foreach (var kit in catalogue.KitLines)
            writer.WriteLine($"kit {kit.Name} kind={CatalogueText.Kind(kit.Kind)}" +
                             (kit.DisplayName == kit.Name ? "" : $" name={CatalogueText.Name(kit.DisplayName)}") +
                             $" options={string.Join(",", kit.Options)}");
        foreach (var item in catalogue.ItemLines)
            writer.WriteLine($"item {item.Name}" + (item.DisplayName == item.Name ? "" : $" name={CatalogueText.Name(item.DisplayName)}") +
                             (item.Paint.TryGet(out var paint) ? $" paint={paint.Colour} acts={paint.Acts}" : "") +
                             (item.Labeller ? " labeller=yes" : ""));
        foreach (var piece in catalogue.Pieces)
        {
            var f = piece.Facts;
            writer.Write($"piece {piece.Name}");
            for (var i = 0; i < f.Kits.Count; i++)
                writer.Write(i == 0
                    ? $" kit={piece.KitNames[0]} option={f.Kits[0].Option.Index}"
                    : $"{(i == 1 ? " also=" : ",")}{piece.KitNames[i]}:{f.Kits[i].Option.Index}");
            writer.Write($" grid={(f.Grid == GridSize.Large ? "large" : "small")}");
            if (piece.DisplayName != piece.Name)
                writer.Write($" name={CatalogueText.Name(piece.DisplayName)}");
            if (f.SupportGroup.TryGet(out var support))
                writer.Write($" support={support.Value}");
            if (f.Pipe.TryGet(out var content))
                writer.Write($" pipe={CatalogueText.Pipe(content)}");
            if (f.Opening.TryGet(out var opening))
                writer.Write($" opening={CatalogueText.Opening(opening)}");
            if (f.Merge.TryGet(out var merge))
                writer.Write($" merge={piece.ItemNames[merge.OffhandTool]}");
            if (f.Keeps != Keeps.None)
                writer.Write($" keeps={CatalogueText.Keeps(f.Keeps)}");
            if (f.Supplies)
                writer.Write(" supplies=yes");
            if (f.ControlFace.TryGet(out var control))
                writer.Write($" control={CatalogueText.Direction(control)}");
            if (f.Vents)
                writer.Write(" vents=yes");
            if (f.Family != PieceFamilies.Other)
                writer.Write($" family={f.Family.Value}");
            if (f.Face.TryGet(out var onFace))
                writer.Write($" place={CatalogueText.Placement(onFace.Kind)}");
            if (!f.Turning.IsFree || f.Turning.Placement != PlacedBy.Grid)
                writer.Write($" turning={CatalogueText.Turning(f.Turning)}");
            writer.WriteLine();
            foreach (var step in f.Steps)
            {
                writer.Write("  step");
                if (step.EntryTool.TryGet(out var entry))
                    writer.Write($" entry={piece.ItemNames[entry]}");
                if (step.Material.TryGet(out var material))
                    writer.Write($" material={piece.ItemNames[material.Item]}*{material.Count}");
                if (step.ExitTool.TryGet(out var exit))
                    writer.Write($" exit={piece.ItemNames[exit]}");
                writer.Write(step.BlocksAir ? " air=yes" : " air=no");
                writer.WriteLine(step.BlocksGravity ? " gravity=yes" : "");
            }

            foreach (var end in f.Ends)
                writer.WriteLine($"  end {end.At.X} {end.At.Y} {end.At.Z} {CatalogueText.Direction(end.Facing)} {CatalogueText.Kinds(end.Kinds)}" +
                                 (end.Reach.TryGet(out var reach) ? $" reach={reach.Value}" : "") +
                                 (end.Role.TryGet(out var role) ? $" role={CatalogueText.Role(role)}" : "") +
                                 (end.GameIndex.TryGet(out var index) ? $" index={index}" : "") +
                                 (end.GameName.TryGet(out var gameName) ? $" game={CatalogueText.Name(gameName)}" : ""));
            foreach (var cell in f.Footprint)
                writer.WriteLine($"  cell {cell.At.X} {cell.At.Y} {cell.At.Z} {CatalogueText.Slot(cell.Slot)}");
            if (f.Collision != SmallCollision.None)
                writer.WriteLine($"  collision {(int)f.Collision}");
            if (f.Face.TryGet(out var faceFacts))
            {
                foreach (var point in faceFacts.Points)
                    writer.WriteLine($"  block {point.X} {point.Y} {point.Z}");
                writer.WriteLine($"  centre {Number(faceFacts.Centre.X)} {Number(faceFacts.Centre.Y)} {Number(faceFacts.Centre.Z)}");
            }
            if (f.Look.TryGet(out var look))
                writer.WriteLine($"  look {CatalogueText.Look(look)}");
            if (f.Ratings.Cable.IsSome || f.Ratings.Pipe.IsSome || f.Ratings.Face.IsSome)
            {
                writer.Write("  rating");
                if (f.Ratings.Cable.TryGet(out var cable))
                    writer.Write($" cable={Number(cable.Value)}");
                if (f.Ratings.Pipe.TryGet(out var pipe))
                    writer.Write($" pipe={Number(pipe.Value)}");
                if (f.Ratings.Face.TryGet(out var face))
                    writer.Write($" face={Number(face.Value)}");
                writer.WriteLine();
            }
        }
    }

    private static string? StartPiece(string[] words, ref Draft? draft, List<NamedPiece> pieces)
    {
        if (draft is not null && draft.Finish(pieces) is { } problem)
            return problem;
        if (words.Length < 2)
            return "piece needs a name";
        var fields = Fields(words, 2);
        if (fields is null)
            return "expected key=value fields";
        if (!fields.TryGetValue("grid", out var gridText))
            return "piece needs grid=";
        GridSize? grid = gridText switch { "small" => GridSize.Small, "large" => GridSize.Large, _ => null };
        if (grid is null)
            return $"bad grid '{gridText}'";

        var kits = new List<(string Name, KitOption Option)>();
        var hasKit = fields.TryGetValue("kit", out var kit);
        var hasOption = fields.TryGetValue("option", out var optionText);
        if (hasKit != hasOption)
            return "kit= and option= go together";
        if (hasKit)
        {
            if (Option(optionText!) is not { } option)
                return $"bad option '{optionText}'";
            kits.Add((kit!, option));
        }

        if (fields.TryGetValue("also", out var alsoText))
        {
            if (!hasKit)
                return "also= needs kit=";
            foreach (var place in alsoText.Split(','))
            {
                var colon = place.LastIndexOf(':');
                if (colon <= 0 || Option(place[(colon + 1)..]) is not { } other)
                    return $"bad also '{place}'";
                kits.Add((place[..colon], other));
            }
        }

        var supportGroup = Option<RuleId>.None;
        if (fields.TryGetValue("support", out var supportText))
        {
            if (!SupportGroups.Parse(supportText).TryGet(out var rule))
                return $"bad support '{supportText}'";
            supportGroup = Lattice.Api.Option.Some(rule);
        }

        var pipeContent = Option<PipeContent>.None;
        if (fields.TryGetValue("pipe", out var pipeText))
        {
            if (CatalogueText.ParsePipe(pipeText) is not { } content)
                return $"bad pipe '{pipeText}'";
            pipeContent = Lattice.Api.Option.Some(content);
        }

        var openingKind = Option<OpeningKind>.None;
        if (fields.TryGetValue("opening", out var openingText))
        {
            if (CatalogueText.ParseOpening(openingText) is not { } kind)
                return $"bad opening '{openingText}'";
            openingKind = Lattice.Api.Option.Some(kind);
        }

        var mergeTool = fields.TryGetValue("merge", out var mergeText) ? mergeText : null;
        if (mergeTool is { Length: 0 })
            return "merge= needs an item";
        var safety = new SafetyWords(Keeps.None, false, Option<Direction>.None, false);
        if (fields.TryGetValue("keeps", out var keepsText))
        {
            if (CatalogueText.ParseKeeps(keepsText) is not { } keeps)
                return $"bad keeps '{keepsText}'";
            safety = safety with { Keeps = keeps };
        }

        if (fields.TryGetValue("supplies", out var suppliesText))
        {
            if (suppliesText != "yes")
                return $"bad supplies '{suppliesText}'";
            safety = safety with { Supplies = true };
        }

        if (fields.TryGetValue("control", out var controlText))
        {
            if (CatalogueText.ParseDirection(controlText) is not { } face)
                return $"bad control '{controlText}'";
            safety = safety with { ControlFace = Lattice.Api.Option.Some(face) };
        }

        if (fields.TryGetValue("vents", out var ventsText))
        {
            if (ventsText != "yes")
                return $"bad vents '{ventsText}'";
            safety = safety with { Vents = true };
        }

        var family = PieceFamilies.Other;
        if (fields.TryGetValue("family", out var familyText) && !CatalogueText.ParseFamily(familyText).TryGet(out family))
            return $"bad family '{familyText}'";

        FacePlacement? placement = null;
        if (fields.TryGetValue("place", out var placeText) && (placement = CatalogueText.ParsePlacement(placeText)) is null)
            return $"bad place '{placeText}'";

        var displayName = words[1];
        if (fields.TryGetValue("name", out var nameText) && !CatalogueText.ParseName(nameText).TryGet(out displayName))
            return $"bad name '{nameText}'";

        var snap = PieceSnap.Of(grid.Value.ToPiece());
        if (fields.TryGetValue("snap", out var snapText) && !CatalogueText.ParseSnap(snapText).TryGet(out snap))
            return $"bad snap '{snapText}'";

        var turning = TurnLimits.Free;
        if (fields.TryGetValue("turning", out var turningText) && !CatalogueText.ParseTurning(turningText).TryGet(out turning))
            return $"bad turning '{turningText}'";
        if (placement is { } placed && turningText is null)
            turning = turning with { Placement = (PlacedBy)(int)placed };
        else if (placement is { } given && turning.Placement != (PlacedBy)(int)given)
            return $"place '{placeText}' and turning '{turningText}' disagree";

        draft = new Draft(words[1], kits, grid.Value, supportGroup, pipeContent, openingKind, mergeTool, safety, family)
        {
            DisplayName = displayName,
            Placement = placement,
            Snap = snap,
            Turning = turning,
        };
        return null;
    }

    private static KitOption? Option(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && KitOption.Create(index).TryGet(out var option)
            ? option
            : null;

    private static Dictionary<string, string>? Fields(string[] words, int from)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = from; i < words.Length; i++)
        {
            var eq = words[i].IndexOf('=');
            if (eq <= 0 || !fields.TryAdd(words[i][..eq], words[i][(eq + 1)..]))
                return null;
        }

        return fields;
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) &&
        !double.IsInfinity(value);

    private static Result<FakeCatalogue, FileError> Fail(string file, int line, string problem) =>
        Result<FakeCatalogue, FileError>.Error(new FileError(file, line, problem));

    /// <summary>The build safety words of a piece line.</summary>
    private sealed record SafetyWords(Keeps Keeps, bool Supplies, Option<Direction> ControlFace, bool Vents);

    private sealed class Draft(
        string name,
        List<(string Name, KitOption Option)> kits,
        GridSize grid,
        Option<RuleId> supportGroup,
        Option<PipeContent> pipe,
        Option<OpeningKind> opening,
        string? mergeTool,
        SafetyWords safety,
        PieceFamily family)
    {
        private readonly List<BuildStep> _steps = [];
        private readonly List<ConnectionEnd> _ends = [];
        private readonly List<FootprintCell> _cells = [];
        private readonly Dictionary<ItemKey, string> _items = [];
        private Ratings _ratings = new(Option<Watts>.None, Option<Kilopascals>.None, Option<Kilopascals>.None);
        private SmallCollision _collision;
        private Option<LookFacts> _look;

        public string? DisplayName { get; init; }

        public FacePlacement? Placement { get; init; }

        public PieceSnap Snap { get; init; }

        public TurnLimits Turning { get; init; } = TurnLimits.Free;

        private readonly List<CellOffset> _points = [];
        private Vector? _centre;

        public string? AddBlock(string[] words)
        {
            if (Placement is null)
                return "block needs place= on its piece";
            if (words.Length != 4 || !int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
                !int.TryParse(words[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
                !int.TryParse(words[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z))
                return "block needs x y z in decimetres";
            _points.Add(new CellOffset(x, y, z));
            return null;
        }

        public string? SetCentre(string[] words)
        {
            if (Placement is null)
                return "centre needs place= on its piece";
            if (_centre is not null)
                return "centre given twice";
            if (words.Length != 4 || !TryNumber(words[1], out var x) || !TryNumber(words[2], out var y) || !TryNumber(words[3], out var z))
                return "centre needs x y z in metres";
            _centre = new Vector(x, y, z);
            return null;
        }

        public string? AddStep(string[] words)
        {
            var fields = Fields(words, 1);
            if (fields is null || !fields.TryGetValue("air", out var air) || air is not ("yes" or "no"))
                return "step needs air=yes|no and key=value fields";
            var gravity = fields.TryGetValue("gravity", out var gravityText) ? gravityText : "no";
            if (gravity is not ("yes" or "no"))
                return $"bad gravity '{gravity}'";
            var entry = Item(fields, "entry");
            var exit = Item(fields, "exit");
            var material = Option<ItemQuantity>.None;
            if (fields.TryGetValue("material", out var materialText))
            {
                var star = materialText.IndexOf('*');
                if (star <= 0 || !int.TryParse(materialText[(star + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
                    !ItemQuantity.Create(Remember(materialText[..star]), count).TryGet(out var quantity))
                    return $"bad material '{materialText}'";
                material = Lattice.Api.Option.Some(quantity);
            }

            if (!StepIndex.Create(_steps.Count).TryGet(out var index))
                return "bad step index";
            _steps.Add(new BuildStep(index, entry, material, exit, air == "yes") { BlocksGravity = gravity == "yes" });
            return null;
        }

        public string? AddEnd(string[] words)
        {
            if (words.Length < 6 || !int.TryParse(words[1], out var x) || !int.TryParse(words[2], out var y) ||
                !int.TryParse(words[3], out var z))
                return "end needs x y z direction kinds [reach=mm] [role=...] [index=n] [game=name]";
            if (CatalogueText.ParseDirection(words[4]) is not { } facing || !CatalogueText.ParseKinds(words[5]).TryGet(out var kinds))
                return $"bad end direction '{words[4]}' or kinds '{words[5]}'";
            var fields = Fields(words, 6);
            if (fields is null)
                return "end options are key=value";
            var reach = Option<Millimetres>.None;
            if (fields.TryGetValue("reach", out var reachText))
            {
                if (!int.TryParse(reachText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm) || mm < 0)
                    return $"bad reach '{reachText}'";
                reach = Lattice.Api.Option.Some(new Millimetres(mm));
            }

            var role = Option<ConnectionRole>.None;
            if (fields.TryGetValue("role", out var roleText))
            {
                if (CatalogueText.ParseRole(roleText) is not { } given)
                    return $"bad role '{roleText}'";
                role = Lattice.Api.Option.Some(given);
            }

            var index = Option<int>.None;
            if (fields.TryGetValue("index", out var indexText))
            {
                if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) || at < 0)
                    return $"bad index '{indexText}'";
                index = Lattice.Api.Option.Some(at);
            }

            var gameName = Option<string>.None;
            if (fields.TryGetValue("game", out var gameText))
            {
                if (!CatalogueText.ParseName(gameText).TryGet(out var name))
                    return $"bad game name '{gameText}'";
                gameName = Lattice.Api.Option.Some(name);
            }

            _ends.Add(new ConnectionEnd(new CellOffset(x, y, z), facing, kinds)
            {
                Reach = reach,
                Role = role,
                GameIndex = index,
                GameName = gameName,
            });
            return null;
        }

        public string? AddCell(string[] words)
        {
            if (words.Length != 5 || !int.TryParse(words[1], out var x) || !int.TryParse(words[2], out var y) ||
                !int.TryParse(words[3], out var z) || CatalogueText.ParseSlot(words[4]) is not { } slot)
                return "cell needs x y z slot";
            _cells.Add(new FootprintCell(new CellOffset(x, y, z), slot));
            return null;
        }

        public string? SetCollision(string[] words)
        {
            if (words.Length != 2 || !int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) ||
                bits < 0 || (bits & ~(int)SmallCollision.Everything) != 0)
                return "collision needs bits 0-31";
            _collision = (SmallCollision)bits;
            return null;
        }

        public string? SetLook(string[] words)
        {
            if (_look.IsSome)
                return "look given twice";
            if (!CatalogueText.ParseLook(words, 1).TryGet(out var look))
                return "look needs a box (six numbers, sizes not negative) and meshes <n> <readable> <by-step>, blueprint readable|unreadable, box or cell";
            _look = Lattice.Api.Option.Some(look);
            return null;
        }

        public string? SetRating(string[] words)
        {
            var fields = Fields(words, 1);
            if (fields is null)
                return "rating needs key=value fields";
            var ratings = _ratings;
            foreach (var (key, text) in fields)
            {
                if (!TryNumber(text, out var value))
                    return $"bad rating '{text}'";
                Ratings? next = key switch
                {
                    "cable" => ratings with { Cable = Lattice.Api.Option.Some(new Watts(value)) },
                    "pipe" => ratings with { Pipe = Lattice.Api.Option.Some(new Kilopascals(value)) },
                    "face" => ratings with { Face = Lattice.Api.Option.Some(new Kilopascals(value)) },
                    _ => null,
                };
                if (next is null)
                    return $"unknown rating '{key}'";
                ratings = next;
            }

            _ratings = ratings;
            return null;
        }

        public string? Finish(List<NamedPiece> pieces)
        {
            if (_steps.Count == 0)
                return $"{name} has no build step";
            var places = new List<KitPlace>(kits.Count);
            var kitNames = new List<string>(kits.Count);
            foreach (var (kitName, option) in kits)
            {
                places.Add(new KitPlace(PrefabHash.Kit(kitName), option));
                kitNames.Add(kitName);
            }

            var merge = mergeTool is null ? Option<MergeFacts>.None : Lattice.Api.Option.Some(new MergeFacts(Remember(mergeTool)));

            pieces.Add(new NamedPiece(
                name,
                ValueList.From(kitNames),
                new PieceFacts(
                    PrefabHash.Piece(name),
                    ValueList.From(places),
                    grid,
                    ValueList.From(_steps),
                    ValueList.From(_ends),
                    _ratings,
                    supportGroup)
                {
                    Footprint = ValueList.From(_cells),
                    Face = Placement is { } kind
                        ? Lattice.Api.Option.Some(new FaceFacts(kind, ValueList.From(_points), _centre ?? default))
                        : Option<FaceFacts>.None,
                    Collision = _collision,
                    Pipe = pipe,
                    Opening = opening,
                    Merge = merge,
                    Keeps = safety.Keeps,
                    Supplies = safety.Supplies,
                    ControlFace = safety.ControlFace,
                    Vents = safety.Vents,
                    Family = family,
                    Look = _look,
                    Snap = Snap,
                    Turning = Turning,
                },
                ValueMap<ItemKey, string>.From(_items)) { DisplayName = DisplayName ?? name });
            return null;
        }

        private Option<ItemKey> Item(Dictionary<string, string> fields, string key) =>
            fields.TryGetValue(key, out var itemName) ? Lattice.Api.Option.Some(Remember(itemName)) : Option<ItemKey>.None;

        private ItemKey Remember(string itemName)
        {
            var key = PrefabHash.Item(itemName);
            _items[key] = itemName;
            return key;
        }
    }
}
