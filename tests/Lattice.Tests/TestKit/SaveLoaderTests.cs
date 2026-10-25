using System.IO;
using System.IO.Compression;
using System.Text;
using Lattice.Api;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.Fixtures;
using Lattice.TestKit.Grid;
using Lattice.TestKit.Saves;

namespace Lattice.Tests.TestKit;

/// <summary>The save loader reads a world.xml's structures into a world, read only.</summary>
public sealed class SaveLoaderTests
{
    private const string Save = "Vulcan2_4.save";

    // A hand-written world.xml in the save's shape: a painted frame, a cable turned a quarter, an item (not a
    // structure), a broken structure, an unknown piece.
    private const string WorldXml = """
        <?xml version="1.0"?>
        <WorldData xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <AllThings>
            <ThingSaveData xsi:type="StructureSaveData">
              <ReferenceId>11</ReferenceId><PrefabName>StructureFrameIron</PrefabName><CustomName>Store</CustomName>
              <IsCustomName>true</IsCustomName><CustomColorIndex>3</CustomColorIndex><CurrentBuildState>2</CurrentBuildState>
              <RegisteredWorldPosition><x>443</x><y>2213</y><z>1623</z></RegisteredWorldPosition>
              <RegisteredWorldRotation><x>0</x><y>0</y><z>0</z><w>1</w></RegisteredWorldRotation>
            </ThingSaveData>
            <ThingSaveData xsi:type="CableSaveSaveData">
              <ReferenceId>12</ReferenceId><PrefabName>StructureCableStraight</PrefabName><CustomName />
              <IsCustomName>false</IsCustomName><CustomColorIndex>-1</CustomColorIndex><CurrentBuildState>0</CurrentBuildState>
              <RegisteredWorldPosition><x>723</x><y>200</y><z>675.5</z></RegisteredWorldPosition>
              <RegisteredWorldRotation><x>2.8E-07</x><y>0.7071083</y><z>-2.6E-07</z><w>-0.7071053</w></RegisteredWorldRotation>
            </ThingSaveData>
            <ThingSaveData xsi:type="StackableSaveData">
              <ReferenceId>13</ReferenceId><PrefabName>ItemIronSheets</PrefabName>
            </ThingSaveData>
            <ThingSaveData xsi:type="StructureSaveData">
              <ReferenceId>14</ReferenceId><PrefabName>StructureWallIron</PrefabName><CurrentBuildState>-1</CurrentBuildState>
              <RegisteredWorldPosition><x>1</x><y>1</y><z>1</z></RegisteredWorldPosition>
              <RegisteredWorldRotation><x>0</x><y>0</y><z>0</z><w>1</w></RegisteredWorldRotation>
            </ThingSaveData>
            <ThingSaveData xsi:type="StructureSaveData">
              <ReferenceId>15</ReferenceId><PrefabName>StructureSomethingModded</PrefabName><CurrentBuildState>0</CurrentBuildState>
              <RegisteredWorldPosition><x>3</x><y>1</y><z>1</z></RegisteredWorldPosition>
              <RegisteredWorldRotation><x>0</x><y>0</y><z>0</z><w>1</w></RegisteredWorldRotation>
            </ThingSaveData>
          </AllThings>
        </WorldData>
        """;

    [Fact]
    public void Structures_are_counted_items_are_not()
    {
        var loaded = Load(WorldXml);
        Assert.Equal(4, loaded.Structures);
        Assert.Equal(4, loaded.World.Count);
        Assert.True(loaded.World.Structure(new ReferenceId(14)).TryGet(out var wall));
        Assert.IsType<BuildState.Broken>(wall.State);
        Assert.Equal(new[] { "StructureSomethingModded" }, loaded.UnknownPieces);
    }

    [Fact]
    public void A_structure_keeps_its_piece_cell_turn_step_colour_and_name()
    {
        var w = Load(WorldXml).World;

        Assert.True(w.Structure(new ReferenceId(11)).TryGet(out var frame));
        Assert.Equal(PrefabHash.Piece("StructureFrameIron"), frame.Ref.Piece);
        Assert.Equal(new Cell(4430, 22130, 16230), frame.Ref.At);
        Assert.Equal(Turn.None, frame.Turn);
        Assert.IsType<BuildState.Built>(frame.State);
        Assert.Equal(3, frame.Colour.Reduce(default(PaintColour)).Index);
        Assert.Equal("Store", frame.Name.Map(n => n.Value).Reduce(""));

        Assert.True(w.Structure(new ReferenceId(12)).TryGet(out var cable));
        Assert.Equal(new Cell(7230, 2000, 6755), cable.Ref.At);
        Assert.NotEqual(Turn.None, cable.Turn);
        Assert.True(cable.Colour.IsNone);
        Assert.True(cable.Name.IsNone);
    }

    [Fact]
    public void Quarter_turn_quaternions_map_to_turns_and_others_do_not()
    {
        Assert.Equal(Option.Some(Turn.None), TurnTable.FromQuaternion(0, 0, 0, 1));
        Assert.True(TurnTable.FromQuaternion(0, 0.7071068, 0, 0.7071068).IsSome);
        Assert.True(TurnTable.FromQuaternion(0, 0.3826834, 0, 0.9238795).IsNone);
    }

    [SaveFact(Save)]
    public void A_loaded_saves_structure_count_equals_the_count_in_its_world_xml()
    {
        var path = Fixtures.Save(Save).Reduce("");
        var loaded = SaveLoader.Load(path, Fixtures.Catalogue());

        Assert.True(loaded.TryGet(out var save), loaded.ToString());
        Assert.Equal(CountBuildStates(path), save.Structures);
    }

    [SaveFact(Save)]
    public void A_loaded_save_passes_the_world_view_checks_on_its_networks()
    {
        Assert.True(SaveLoader.Load(Fixtures.Save(Save).Reduce(""), Fixtures.Catalogue()).TryGet(out var save));
        foreach (var (network, info) in save.World.AllNetworks)
            foreach (var piece in info.Pieces)
                Assert.Equal(Option.Some(network), save.World.NetworkOf(piece));
    }

    private static LoadedSave Load(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = SaveLoader.Read(stream, "world.xml", FakeCatalogue.Hand());
        Assert.True(result.TryGet(out var loaded), result.ToString());
        return loaded;
    }

    /// <summary>Counts structure elements in the save's world.xml as text, independently of the loader.</summary>
    private static int CountBuildStates(string savePath)
    {
        using var zip = ZipFile.OpenRead(savePath);
        using var reader = new StreamReader(zip.GetEntry("world.xml")!.Open());
        var count = 0;
        while (reader.ReadLine() is { } line)
            if (line.Contains("<CurrentBuildState>"))
                count++;
        return count;
    }
}
