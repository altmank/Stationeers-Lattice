using System;
using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// The reader finds doors and windows by the piece's class chain, so a modded subclass is what its base
/// is (decompile <c>Assets.Scripts.Objects.Structures/Door.cs</c>, <c>WindowShutter.cs</c>, <c>RoboticArmDoor.cs</c>;
/// <c>Assets.Scripts.Objects/WallTransparent.cs</c>, <c>ShutteredWindow.cs</c>).
/// </summary>
public sealed class OpeningKindTests
{
    private const string DoorType = "Assets.Scripts.Objects.Structures.Door";
    private const string DeviceType = "Assets.Scripts.Objects.Pipes.Device";
    private const string StructureType = "Assets.Scripts.Objects.Structure";
    private const string WallType = "Assets.Scripts.Objects.Wall";
    private const string WindowType = "Assets.Scripts.Objects.WallTransparent";

    public static TheoryData<string, string[], string> Chains() => new()
    {
        { "StructureCompositeDoor", new[] { DoorType, DeviceType, StructureType }, "door" },
        { "StructureAirlock", new[] { "Assets.Scripts.Objects.Structures.Airlock", DoorType, DeviceType, StructureType }, "door" },
        { "StructureRoboticArmDoor", new[] { "Assets.Scripts.Objects.Structures.RoboticArmDoor", DeviceType, StructureType }, "door" },
        { "StructureWindowShutter", new[] { "Assets.Scripts.Objects.Structures.WindowShutter", DoorType, DeviceType, StructureType }, "window" },
        { "StructureCompositeWindow", new[] { WindowType, WallType, StructureType }, "window" },
        { "StructureShutteredWindow", new[] { "Assets.Scripts.Objects.ShutteredWindow", WindowType, WallType, StructureType }, "window" },
        { "ModHatch", new[] { "SomeMod.Hatch", DoorType, DeviceType, StructureType }, "door" },
        { "ModPorthole", new[] { "SomeMod.Porthole", WindowType, WallType, StructureType }, "window" },
        { "StructureWallIron", new[] { WallType, StructureType }, "none" },
        { "ModThing", new[] { "SomeMod.Thing", StructureType }, "none" },
    };

    [Theory]
    [MemberData(nameof(Chains))]
    public void The_opening_kind_follows_the_class_chain(string name, string[] chain, string expected)
    {
        var facts = Map(Piece(name, grid: 2, types: Array.ConvertAll(chain, t => Type(t))));

        Assert.Equal(expected, facts.Opening.Map(static k => CatalogueText.Opening(k)).Reduce("none"));
    }

    [Fact]
    public void The_opening_kind_survives_the_catalogue_file()
    {
        var door = Piece("ModHatch", grid: 2, types: new[] { Type("SomeMod.Hatch"), Type(DoorType), Type(StructureType, true) });
        var window = Piece("StructureCompositeWindow", grid: 2, types: new[] { Type(WindowType), Type(StructureType, true) });
        var wall = Piece("StructureWallIron", grid: 2, types: new[] { Type(WallType), Type(StructureType, true) });
        var catalogue = new PrefabCatalogue(new CountingSource(new[] { Kit("ItemKitWalls", door, window, wall) }, new[] { door, window, wall }));
        var text = new StringWriter();
        CatalogueDataFile.Write(catalogue, catalogue.ReadAll(), TimeSpan.FromMilliseconds(1), text);

        var read = CatalogueFile.Read(new StringReader(text.ToString()), "openings.lcat").TryGet(out var file) ? file : throw new InvalidOperationException(text.ToString());

        Assert.Equal(Option.Some(OpeningKind.Door), read.Named("ModHatch").Opening);
        Assert.Equal(Option.Some(OpeningKind.Window), read.Named("StructureCompositeWindow").Opening);
        Assert.True(read.Named("StructureWallIron").Opening.IsNone);
    }

    private static PieceFacts Map(PrefabData prefab) =>
        PieceMapper.Map(prefab, ValueList<KitPlace>.Empty).TryGet(out var facts) ? facts : throw new InvalidOperationException(prefab.Name);
}
