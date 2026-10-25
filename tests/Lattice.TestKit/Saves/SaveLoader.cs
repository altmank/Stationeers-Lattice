using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.Grid;
using Lattice.TestKit.World;

namespace Lattice.TestKit.Saves;

/// <summary>
/// A save read into a world. <see cref="Structures"/> counts every structure in the save, broken ones included (build
/// state below 0, <see cref="BuildState.Broken"/>). <see cref="UnknownPieces"/> names the prefabs the catalogue does not
/// hold; their structures are in the world, never fully built (the catalogue knows a piece's last step), and on no
/// network.
/// </summary>
internal sealed record LoadedSave(FakeWorld World, ValueSet<string> UnknownPieces)
{
    public int Structures => World.Count;
}

/// <summary>
/// Reads the structures of a game save into a <see cref="FakeWorld"/>: a <c>.save</c> (a zip holding
/// <c>world.xml</c>) or a bare <c>world.xml</c>. Never writes. A structure is every <c>ThingSaveData</c> with a
/// <c>CurrentBuildState</c> (decompile Assets.Scripts.Objects/StructureSaveData.cs: StructureSaveData and its
/// subclasses carry it). Read per structure: <c>ReferenceId</c>; <c>PrefabName</c> (hashed, <see cref="PrefabHash"/>);
/// the cell from <c>RegisteredWorldPosition</c> as the game's Grid3 key, round(position * 10) per axis;
/// the turn from <c>RegisteredWorldRotation</c>; the build
/// state (below 0 is broken: ThingDamageState.Destroy); <c>CustomColorIndex</c> (below 0 is unpainted);
/// <c>CustomName</c> when not empty (the game writes <c>IsCustomName</c> false even for a named structure: it never sets
/// it, <c>Thing.InitialiseSaveData</c>); the labeller setting from a <c>Setting</c> element (logic units and the
/// other save data classes that name it so; a class that saves it under another name, as the transformer's
/// <c>OutputSetting</c>, reads as none). Networks come from the catalogue's ends.
/// </summary>
internal static class SaveLoader
{
    public static Result<LoadedSave, FileError> Load(string path, ICatalogue catalogue)
    {
        if (!File.Exists(path))
            return Fail(path, 0, "no such file");
        if (!path.EndsWith(".save", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(path);
            return Read(file, path, catalogue);
        }

        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("world.xml");
        if (entry is null)
            return Fail(path, 0, "no world.xml in the save");
        using var stream = entry.Open();
        return Read(stream, path, catalogue);
    }

    public static Result<LoadedSave, FileError> Read(Stream worldXml, string name, ICatalogue catalogue)
    {
        var world = new FakeWorld(catalogue);
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        using var reader = XmlReader.Create(worldXml, new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit });
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "ThingSaveData")
            {
                reader.Read();
                continue;
            }

            var line = ((IXmlLineInfo)reader).LineNumber;
            var thing = (XElement)XNode.ReadFrom(reader);
            if (thing.Element("CurrentBuildState") is not { } stateElement)
                continue;
            var problem = ReadStructure(thing, stateElement, catalogue, world, unknown);
            if (problem is not null)
                return Fail(name, line, problem);
        }

        return Result<LoadedSave, FileError>.Ok(new LoadedSave(world, ValueSet.From(unknown)));
    }

    private static string? ReadStructure(
        XElement thing,
        XElement stateElement,
        ICatalogue catalogue,
        FakeWorld world,
        HashSet<string> unknown)
    {
        if (!long.TryParse(thing.Element("ReferenceId")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var refValue))
            return "no ReferenceId";
        var prefabName = thing.Element("PrefabName")?.Value;
        if (string.IsNullOrEmpty(prefabName))
            return "no PrefabName";
        if (!int.TryParse(stateElement.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var state))
            return "bad CurrentBuildState";
        var id = new ReferenceId(refValue);

        var position = thing.Element("RegisteredWorldPosition") ?? thing.Element("WorldPosition");
        var rotation = thing.Element("RegisteredWorldRotation") ?? thing.Element("WorldRotation");
        if (position is null || rotation is null)
            return "no position or rotation";
        if (!TryVector(position, out var x, out var y, out var z))
            return "bad position";
        if (!TryQuaternion(rotation, out var qx, out var qy, out var qz, out var qw) ||
            !TurnTable.FromQuaternion(qx, qy, qz, qw).TryGet(out var turn))
            return "rotation is not a cube turn";

        var piece = PrefabHash.Piece(prefabName);
        var facts = catalogue.Piece(piece).ToOption();
        if (facts.IsNone)
            unknown.Add(prefabName);
        BuildState buildState = StepIndex.Create(state).TryGet(out var step)
            ? BuildStates.At(step, facts)
            : new BuildState.Broken();

        var colour = int.TryParse(thing.Element("CustomColorIndex")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var colourIndex)
            ? PaintColour.Create(colourIndex).ToOption()
            : Option<PaintColour>.None;
        var name = thing.Element("CustomName")?.Value is { Length: > 0 } customName
            ? ThingName.Create(customName).ToOption()
            : Option<ThingName>.None;

        var cell = new Cell(Key(x), Key(y), Key(z));
        var setting = double.TryParse(thing.Element("Setting")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var settingValue)
            ? Setting.Create(settingValue).ToOption()
            : Option<Setting>.None;
        world.Load(new StructureInfo(new StructureRef(id, piece, cell), turn, buildState, colour, name, setting));
        return null;
    }

    private static int Key(double metres) => (int)Math.Round(metres * 10);

    private static bool TryVector(XElement e, out double x, out double y, out double z)
    {
        y = z = 0;
        return TryNumber(e, "x", out x) && TryNumber(e, "y", out y) && TryNumber(e, "z", out z);
    }

    private static bool TryQuaternion(XElement e, out double x, out double y, out double z, out double w)
    {
        w = 0;
        return TryVector(e, out x, out y, out z) && TryNumber(e, "w", out w);
    }

    private static bool TryNumber(XElement parent, string name, out double value) =>
        double.TryParse(parent.Element(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static Result<LoadedSave, FileError> Fail(string file, int line, string problem) =>
        Result<LoadedSave, FileError>.Error(new FileError(file, line, problem));
}
