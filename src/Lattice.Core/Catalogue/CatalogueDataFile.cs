using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Catalogue;

/// <summary>
/// Writes the catalogue data file the test kit loads (format <c>lattice-catalogue 3</c>, grammar in the test kit's
/// <c>CatalogueFile</c>): every kit with its kind, display name and every option in scroll order, then every piece that
/// mapped, once, under the kit its first build step uses (its other kits after <c>also=</c>), then the pieces no kit
/// places, with their display names, build steps, ends, footprint, ratings, support group, family, face placement and
/// look. Display names, family, face placement, look, and an end's game index (<c>index=</c>) and kind name
/// (<c>game=</c>) are optional: a file without them reads names as prefab names, family <c>other</c>, every piece placed
/// in a cell, no look, and each end numbered by its place among its piece's ends. Comments at the top say it came from the game, how long the load took, and what was left out.
/// </summary>
internal static class CatalogueDataFile
{
    /// <summary>The first word of the file; the number after it is the format.</summary>
    public const string Magic = "lattice-catalogue";

    /// <summary>
    /// The format this writer writes and the test kit reads. Format 2 added end kinds, roles, footprints and
    /// collision masks; format 3 added kit lines and display names. A file of another format is refused, never read
    /// as this one.
    /// </summary>
    public const int Format = 3;

    public const string Header = "lattice-catalogue 3";

    /// <summary>The first comment line of a file written from the game's prefabs.</summary>
    public const string FromGameLine = "# from-game Lattice.Game catalogue export";

    public static void Write(PrefabCatalogue catalogue, ReadReport report, TimeSpan loadTime, TextWriter writer)
    {
        writer.Write(Header + "\n");
        writer.Write(FromGameLine + "\n");
        writer.Write("# load-ms " + ((long)loadTime.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "\n");
        writer.Write("# pieces " + report.Read.ToString(CultureInfo.InvariantCulture) + "\n");
        foreach (var reason in report.Refused)
            writer.Write("# refused " + Fact(reason, "piece") + " " + Fact(reason, "problem") + "\n");
        foreach (var name in report.WithoutKit)
            writer.Write("# no-kit " + name + "\n");
        if (catalogue.BrokenTools.Count > 0)
        {
            writer.Write("broken-tools ");
            for (var i = 0; i < catalogue.BrokenTools.Count; i++)
                writer.Write((i == 0 ? "" : ",") + ItemName(catalogue, catalogue.BrokenTools[i]));
            writer.Write("\n");
        }

        var listing = catalogue.Listing;
        var names = new Dictionary<PrefabKey, ListedPiece>(listing.Pieces.Count);
        foreach (var piece in listing.Pieces)
            names[piece.Key] = piece;
        foreach (var kit in listing.Kits)
        {
            writer.Write("kit " + kit.PrefabName + " kind=" + CatalogueText.Kind(kit.Kind));
            if (kit.Name != kit.PrefabName)
                writer.Write(" name=" + CatalogueText.Name(kit.Name));
            writer.Write(" options=");
            for (var i = 0; i < kit.Options.Count; i++)
                writer.Write((i == 0 ? "" : ",") + (names.TryGetValue(kit.Options[i], out var option)
                    ? option.PrefabName
                    : catalogue.NameOf(kit.Options[i]).Reduce(Number(kit.Options[i].Hash))));
            writer.Write("\n");
        }

        foreach (var kit in catalogue.Kits)
        {
            if (!catalogue.Kit(kit).TryGet(out var kitFacts))
                continue;
            foreach (var key in kitFacts.Pieces)
                if (catalogue.Piece(key).TryGet(out var facts) && facts.Kits.Count > 0 && facts.Kits[0].Kit == kit &&
                    catalogue.DataOf(key).TryGet(out var data))
                    WritePiece(writer, data, catalogue, facts, names);
        }

        foreach (var key in catalogue.Kitless)
            if (catalogue.Piece(key).TryGet(out var facts) && catalogue.DataOf(key).TryGet(out var data))
                WritePiece(writer, data, catalogue, facts, names);
    }

    private static void WritePiece(TextWriter writer, PrefabData data, PrefabCatalogue catalogue, PieceFacts facts, Dictionary<PrefabKey, ListedPiece> names)
    {
        var items = ItemNames(data);
        writer.Write("\npiece " + data.Name);
        for (var i = 0; i < facts.Kits.Count; i++)
        {
            var place = facts.Kits[i];
            var kitName = catalogue.NameOf(place.Kit).Reduce(Number(place.Kit.Hash));
            writer.Write(i == 0
                ? " kit=" + kitName + " option=" + Number(place.Option.Index)
                : (i == 1 ? " also=" : ",") + kitName + ":" + Number(place.Option.Index));
        }

        writer.Write(facts.Grid == GridSize.Large ? " grid=large" : " grid=small");
        if (names.TryGetValue(data.Key, out var listed) && listed.Name != data.Name)
            writer.Write(" name=" + CatalogueText.Name(listed.Name));
        if (facts.SupportGroup.TryGet(out var support))
            writer.Write(" support=" + support.Value);
        if (facts.Pipe.TryGet(out var pipeContent))
            writer.Write(" pipe=" + CatalogueText.Pipe(pipeContent));
        if (facts.Opening.TryGet(out var opening))
            writer.Write(" opening=" + CatalogueText.Opening(opening));
        if (facts.Merge.TryGet(out var merge))
            writer.Write(" merge=" + ItemName(catalogue, merge.OffhandTool));
        if (facts.Keeps != Keeps.None)
            writer.Write(" keeps=" + CatalogueText.Keeps(facts.Keeps));
        if (facts.Supplies)
            writer.Write(" supplies=yes");
        if (facts.ControlFace.TryGet(out var control))
            writer.Write(" control=" + CatalogueText.Direction(control));
        if (facts.Vents)
            writer.Write(" vents=yes");
        if (facts.Family != PieceFamilies.Other)
            writer.Write(" family=" + facts.Family.Value);
        if (facts.Face.TryGet(out var onFace))
            writer.Write(" place=" + CatalogueText.Placement(onFace.Kind));
        writer.Write("\n");
        foreach (var step in facts.Steps)
        {
            writer.Write("  step");
            if (step.EntryTool.TryGet(out var entry))
                writer.Write(" entry=" + items[entry]);
            if (step.Material.TryGet(out var material))
                writer.Write(" material=" + items[material.Item] + "*" + Number(material.Count));
            if (step.ExitTool.TryGet(out var exit))
                writer.Write(" exit=" + items[exit]);
            writer.Write(step.BlocksAir ? " air=yes" : " air=no");
            writer.Write(step.BlocksGravity ? " gravity=yes\n" : "\n");
        }

        foreach (var end in facts.Ends)
        {
            writer.Write("  end " + Number(end.At.X) + " " + Number(end.At.Y) + " " + Number(end.At.Z) + " " +
                         CatalogueText.Direction(end.Facing) + " " + CatalogueText.Kinds(end.Kinds));
            if (end.Reach.TryGet(out var reach))
                writer.Write(" reach=" + Number(reach.Value));
            if (end.Role.TryGet(out var role))
                writer.Write(" role=" + CatalogueText.Role(role));
            if (end.GameIndex.TryGet(out var index))
                writer.Write(" index=" + Number(index));
            if (end.GameName.TryGet(out var gameName))
                writer.Write(" game=" + CatalogueText.Name(gameName));
            writer.Write("\n");
        }

        foreach (var cell in facts.Footprint)
            writer.Write("  cell " + Number(cell.At.X) + " " + Number(cell.At.Y) + " " + Number(cell.At.Z) + " " + CatalogueText.Slot(cell.Slot) + "\n");
        if (facts.Collision != SmallCollision.None)
            writer.Write("  collision " + Number((int)facts.Collision) + "\n");
        if (facts.Face.TryGet(out var faceFacts))
        {
            foreach (var point in faceFacts.Points)
                writer.Write("  block " + Number(point.X) + " " + Number(point.Y) + " " + Number(point.Z) + "\n");
            writer.Write("  centre " + Number(faceFacts.Centre.X) + " " + Number(faceFacts.Centre.Y) + " " + Number(faceFacts.Centre.Z) + "\n");
        }
        if (facts.Look.TryGet(out var look))
            writer.Write("  look " + CatalogueText.Look(look) + "\n");
        var ratings = facts.Ratings;
        if (ratings.Cable.IsSome || ratings.Pipe.IsSome || ratings.Face.IsSome)
        {
            writer.Write("  rating");
            if (ratings.Cable.TryGet(out var cable))
                writer.Write(" cable=" + Number(cable.Value));
            if (ratings.Pipe.TryGet(out var pipe))
                writer.Write(" pipe=" + Number(pipe.Value));
            if (ratings.Face.TryGet(out var face))
                writer.Write(" face=" + Number(face.Value));
            writer.Write("\n");
        }
    }

    private static Dictionary<ItemKey, string> ItemNames(PrefabData data)
    {
        var names = new Dictionary<ItemKey, string>(data.States.Count * 2);
        foreach (var state in data.States)
        {
            if (state.Entry.TryGet(out var entry))
                names[entry.Item.Key] = entry.Item.Name;
            if (state.Entry2.TryGet(out var entry2))
                names[entry2.Item.Key] = entry2.Item.Name;
            if (state.Exit.TryGet(out var exit))
                names[exit.Key] = exit.Name;
        }

        return names;
    }

    private static string ItemName(PrefabCatalogue catalogue, ItemKey item) => catalogue.NameOf(item).Reduce(Number(item.Hash));

    private static string Fact(Reason reason, string key) => reason.Facts.TryGetValue(key, out var value) ? value : "?";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

}

/// <summary>
/// The headless-run switch: <c>-lattice-write-catalogue &lt;file&gt;</c> on the game's command line makes Lattice
/// write the catalogue data file at load and quit the game.
/// </summary>
internal static class ExportSwitch
{
    public const string Name = "-lattice-write-catalogue";

    /// <summary>The file to write, none when the switch is absent; refused when the switch has no file after it.</summary>
    public static Result<Option<string>, Reason> Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] != Name)
                continue;
            if (i + 1 >= args.Count || args[i + 1].Length == 0 || args[i + 1][0] == '-')
                return Result<Option<string>, Reason>.Error(Reason.Of(
                    ReasonCodes.InvalidValue,
                    CatalogueReasons.Catalogue,
                    ValueMap<string, string>.Empty.With("switch", Name).With("problem", "needs-a-file")));
            return Result<Option<string>, Reason>.Ok(Option.Some(args[i + 1]));
        }

        return Result<Option<string>, Reason>.Ok(Option<string>.None);
    }
}
