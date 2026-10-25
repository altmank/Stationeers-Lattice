using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.Fixtures;
using Lattice.TestKit.Saves;
using Lattice.TestKit.World;

namespace Lattice.Tests.World;

/// <summary>
/// The world bridge's reads of the known world (Lattice.Game.Live on the test instance: a scene built in a new
/// world, saved as lattice-known, and the same save loaded again) match the save loader's <see cref="FakeWorld"/> for
/// that save. Structures match exactly; networks match as sets of pieces (network ids are the game's own).
/// </summary>
public sealed class KnownSaveTests
{
    private const string Save = "lattice-known.save";

    [SaveFact(Save)]
    public void The_world_reads_the_same_after_its_save_is_loaded() =>
        Assert.Equal(File.ReadAllLines(Dump("new")), File.ReadAllLines(Dump("load")));

    [SaveFact(Save)]
    public void Every_structure_the_bridge_read_is_in_the_save_as_the_bridge_read_it()
    {
        var (world, catalogue) = Load();
        var mismatches = new List<string>();
        var structures = Structures();
        foreach (var read in structures)
        {
            if (!world.Structure(read.Ref.Id).TryGet(out var saved))
            {
                mismatches.Add($"{read.Ref.Id.Value}: not in the save");
                continue;
            }

            // A piece the catalogue file does not know has no last step for the loader (a modded one here); a class
            // that saves its setting under another name, or not at all, has none in the save (MatterLink's capacitor).
            var comparable = catalogue.Piece(read.Ref.Piece).IsOk
                ? saved
                : saved with { State = read.State is BuildState.Broken ? saved.State : read.State };
            if (saved.Setting.IsNone)
                comparable = comparable with { Setting = read.Setting };
            if (comparable != read)
                mismatches.Add($"{read.Ref.Id.Value}: bridge {read}, save {saved}");
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
        Assert.Equal(world.Count, structures.Count);
    }

    [SaveFact(Save)]
    public void The_save_holds_the_scenes_part_built_painted_named_broken_modded_and_set_pieces()
    {
        var states = Structures();
        Assert.Contains(states, s => s.State is BuildState.Building);
        Assert.Contains(states, s => s.State is BuildState.Broken);
        Assert.Contains(states, s => s.Colour.IsSome);
        Assert.Contains(states, s => s.Name.Map(n => n.Value).Reduce("") == "Lattice Known");
        Assert.Contains(states, s => s.Ref.Piece == PrefabHash.Piece("StructureMatterlinkCapacitor"));
        Assert.Contains(states, s => s.Setting == Option.Some(Relays.Setting(42)));
    }

    [SaveFact(Save)]
    public void Every_network_the_bridge_read_is_one_network_with_the_same_pieces_in_the_save()
    {
        var (world, _) = Load();
        var mismatches = new List<string>();
        foreach (var line in File.ReadAllLines(Dump("new")).Where(l => l.StartsWith("network ", StringComparison.Ordinal)))
        {
            var parts = line.Split(' ');
            var pieces = parts[2].Split(',').Select(long.Parse).Select(id => new ReferenceId(id)).ToHashSet();
            var networks = pieces.Select(world.NetworkOf).Distinct().ToList();
            if (networks.Count != 1 || !networks[0].TryGet(out var network) || !world.Network(network).TryGet(out var facts))
            {
                mismatches.Add($"{line}: save has {networks.Count} networks");
                continue;
            }

            if (!facts.Pieces.ToHashSet().SetEquals(pieces))
                mismatches.Add($"{line}: save network has {string.Join(",", facts.Pieces.Select(p => p.Value))}");
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    private static (FakeWorld World, Lattice.Core.Ports.ICatalogue Catalogue) Load()
    {
        var catalogue = Fixtures.Catalogue();
        var path = Fixtures.Save(Save).Reduce("");
        var loaded = SaveLoader.Load(path, catalogue).TryGet(out var save) ? save : throw new InvalidOperationException("save did not load");
        return (loaded.World, catalogue);
    }

    private static string Dump(string mode) => Path.Combine(Fixtures.Generated, $"world-dump-{mode}.txt");

    private static List<StructureInfo> Structures() =>
        File.ReadAllLines(Dump("new")).Where(l => l.StartsWith("structure ", StringComparison.Ordinal)).Select(Parse).ToList();

    private static StructureInfo Parse(string line)
    {
        var p = line.Split(' ');
        var at = new Cell(int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]));
        BuildState state = p[7] switch
        {
            "built" => new BuildState.Built(),
            "broken" => new BuildState.Broken(),
            _ => Relays.Building(int.Parse(p[7]["building-".Length..])),
        };
        return new StructureInfo(
            new StructureRef(new ReferenceId(long.Parse(p[1])), new PrefabKey(int.Parse(p[2])), at),
            Turns.Of(int.Parse(p[6])),
            state,
            p[8] == "-" ? Option<PaintColour>.None : Option.Some(Relays.Colour(int.Parse(p[8]))),
            p[9] == "-" ? Option<ThingName>.None : Option.Some(Relays.Name(p[9].Replace('_', ' '))),
            p[10] == "-" ? Option<Setting>.None : Option.Some(Relays.Setting(double.Parse(p[10], System.Globalization.CultureInfo.InvariantCulture))));
    }
}
