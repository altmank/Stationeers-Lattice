using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.Fixtures;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// A fact about the catalogue the game itself wrote (tests/fixtures/generated/catalogue.lcat from a run with
/// <c>-lattice-write-catalogue</c>, copied by make-fixtures.ps1 -Catalogue). Skipped until there is one: Live checks
/// makes it on the test instance.
/// </summary>
public sealed class GameCatalogueFactAttribute : FactAttribute
{
    public GameCatalogueFactAttribute(bool needsModellingKit = false)
    {
        if (GameCatalogue.Path().IsNone)
            Skip = "No catalogue written by the game in tests/fixtures/generated: run the game with " + ExportSwitch.Name +
                   " <file>, then tests/fixtures/make-fixtures.ps1 -Catalogue <file>.";
        else if (needsModellingKit && GameCatalogue.ModellingKitStructures().IsNone)
            Skip = "No LatticeFixtures/catalogue/structures.json above the repo: run the modelling kit's tools/catalogue/build.py " +
                   "with --out <LatticeFixtures>/catalogue.";
    }
}

internal static class GameCatalogue
{
    public static Option<string> Path()
    {
        var path = System.IO.Path.Combine(Fixtures.Generated, Fixtures.CatalogueFileName);
        if (!File.Exists(path))
            return Option<string>.None;
        using var reader = new StreamReader(path);
        reader.ReadLine();
        return reader.ReadLine() == CatalogueDataFile.FromGameLine ? Option.Some(path) : Option<string>.None;
    }

    public static Option<string> ModellingKitStructures()
    {
        for (var dir = new DirectoryInfo(Fixtures.Generated); dir is not null; dir = dir.Parent)
        {
            var path = System.IO.Path.Combine(dir.FullName, "LatticeFixtures", "catalogue", "structures.json");
            if (File.Exists(path))
                return Option.Some(path);
        }

        return Option<string>.None;
    }

    public static FakeCatalogue Load()
    {
        var path = Path().Reduce(() => throw new InvalidOperationException("No game catalogue."));
        using var reader = new StreamReader(path);
        return CatalogueFile.Read(reader, path).TryGet(out var catalogue)
            ? catalogue
            : throw new InvalidOperationException($"{path} does not read.");
    }
}

public sealed class GameCatalogueFacts
{
    // The export writes every kit's Constructables in scroll order (MultiConstructor.cs:15, 36-51); the view
    // over the file lists them all, and a piece's first kit is the one its facts name first.
    [GameCatalogueFact]
    public void Every_kits_options_are_its_exported_constructables_and_each_pieces_first_kit_is_its_facts_first()
    {
        var catalogue = GameCatalogue.Load();
        var view = new PieceCatalogue(catalogue.Listing, new CatalogueAdditions(catalogue));

        Assert.NotEmpty(catalogue.KitLines);
        Assert.Equal(catalogue.KitLines.Count, view.Kits.Count);
        foreach (var line in catalogue.KitLines)
            Assert.Equal(line.Options.Count, Prefabs.Ok(view.Kit(PrefabHash.Kit(line.Name))).Options.Count);
        foreach (var piece in catalogue.Pieces)
            if (piece.Facts.Kits.Count > 0)
                Assert.Equal(piece.Facts.Kits[0], Prefabs.Ok(view.KitOf(piece.Facts.Key)));
        Assert.True(catalogue.Gap.IsNone);
    }

    [GameCatalogueFact]
    public void A_merging_kit_is_listed_and_every_single_kit_has_one_option()
    {
        var lines = GameCatalogue.Load().KitLines;

        Assert.Contains(lines, static l => l.Kind == KitKind.Merge);
        Assert.Contains(lines, static l => l.Kind == KitKind.Single);
        Assert.All(lines.Where(static l => l.Kind == KitKind.Single), static l => Assert.Single(l.Options));
    }

    // StructureFrame (steel) seals at state 2; Iron Wall
    // seals only at its last state.
    [GameCatalogueFact]
    public void Steel_frame_blocks_air_from_its_third_state()
    {
        var steps = GameCatalogue.Load().Named("StructureFrame").Steps;

        for (var i = 0; i < steps.Count; i++)
            Assert.Equal(i >= 2, steps[i].BlocksAir);
    }

    [GameCatalogueFact]
    public void Iron_wall_blocks_air_only_in_its_last_state()
    {
        var steps = GameCatalogue.Load().Named("StructureWallIron").Steps;

        for (var i = 0; i < steps.Count; i++)
            Assert.Equal(i == steps.Count - 1, steps[i].BlocksAir);
    }

    [GameCatalogueFact]
    public void Cables_and_pipes_merge_and_a_frame_does_not()
    {
        var catalogue = GameCatalogue.Load();

        Assert.True(catalogue.Named("StructureCableJunction").Merge.IsSome);
        Assert.Equal(Option.Some(new Lattice.Core.Ports.MergeFacts(PrefabHash.Item("ItemWrench"))), catalogue.Named("StructurePipeStraight").Merge);
        Assert.True(catalogue.Named("StructureFrame").Merge.IsNone);
    }

    [GameCatalogueFact]
    public void The_angle_grinder_and_the_welding_torch_take_broken_structures_apart()
    {
        var tools = GameCatalogue.Load().BrokenTools;

        Assert.Contains(PrefabHash.Item("ItemAngleGrinder"), tools);
        Assert.Contains(PrefabHash.Item("ItemWeldingTorch"), tools);
    }

    // Every piece has a look, its meshes else its blueprint else a box else a cell.
    [GameCatalogueFact]
    public void Every_piece_has_a_look_with_a_box_of_some_size()
    {
        var pieces = GameCatalogue.Load().Pieces;

        Assert.All(pieces, static p => Assert.True(
            p.Facts.Look.Map(static l => l.Bounds.Size.X > 0 && l.Bounds.Size.Y > 0 && l.Bounds.Size.Z > 0).Reduce(false), p.Name));
        Assert.True(pieces.Count(static p => p.Facts.Look.Map(static l => l.Source is LookSource.Meshes).Reduce(false)) >= pieces.Count * 0.95);
    }

    [GameCatalogueFact]
    public void Every_family_found_by_type_has_pieces_in_the_game()
    {
        var families = GameCatalogue.Load().Pieces.Select(static p => p.Facts.Family).ToHashSet();

        foreach (var family in new[] { PieceFamilies.Cable, PieceFamilies.Pipe, PieceFamilies.Chute, PieceFamilies.Frame, PieceFamilies.Wall, PieceFamilies.Device, PieceFamilies.Other })
            Assert.Contains(family, families);
    }

    [GameCatalogueFact]
    public void Base_pieces_land_in_their_families_by_type()
    {
        var catalogue = GameCatalogue.Load();

        Assert.Equal(PieceFamilies.Frame, catalogue.Named("StructureFrame").Family);
        Assert.Equal(PieceFamilies.Wall, catalogue.Named("StructureWallIron").Family);
        Assert.Equal(PieceFamilies.Pipe, catalogue.Named("StructurePipeStraight").Family);
        Assert.Equal(PieceFamilies.Cable, catalogue.Named("StructureCableJunction").Family);
        Assert.Equal(PieceFamilies.Device, catalogue.Named("StructureActiveVent").Family);
    }

    [GameCatalogueFact]
    public void The_file_records_the_load_time()
    {
        var text = File.ReadAllText(GameCatalogue.Path().Reduce(""));

        Assert.Matches(@"(?m)^# load-ms \d+$", text);
    }

    // The modelling kit's extract (StationeersModellingKit tools/catalogue/structures.py, run with --out into
    // LatticeFixtures/catalogue, outside every repo): per state tool_entry, entry_qty, tool_entry2,
    // entry_qty2, tool_exit, read from the assets with UnityPy (an independent extract).
    [GameCatalogueFact(needsModellingKit: true)]
    public void Build_steps_of_every_structure_equal_the_modelling_kit_extract()
    {
        var catalogue = GameCatalogue.Load();
        using var json = JsonDocument.Parse(File.ReadAllText(GameCatalogue.ModellingKitStructures().Reduce("")));
        var structures = json.RootElement.GetProperty("structures");
        var mismatches = new List<string>();
        var compared = 0;
        foreach (var piece in catalogue.Pieces)
        {
            if (!structures.TryGetProperty(piece.Name, out var row))
                continue;
            compared++;
            var states = row.GetProperty("states");
            if (states.GetArrayLength() != piece.Facts.Steps.Count)
            {
                mismatches.Add($"{piece.Name}: {states.GetArrayLength()} states, {piece.Facts.Steps.Count} steps");
                continue;
            }

            for (var i = 0; i < piece.Facts.Steps.Count; i++)
            {
                var problem = Compare(states[i], piece.Facts.Steps[i], piece);
                if (problem is not null)
                    mismatches.Add($"{piece.Name} step {i}: {problem}");
            }
        }

        Assert.True(compared > 500, $"only {compared} structures in both");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(40)));
    }

    private static string? Compare(JsonElement state, Lattice.Core.Ports.BuildStep step, NamedPiece piece)
    {
        var entries = new Dictionary<string, int>();
        foreach (var (tool, quantity) in new[] { ("tool_entry", "entry_qty"), ("tool_entry2", "entry_qty2") })
            if (state.GetProperty(tool).ValueKind == JsonValueKind.String)
                entries[state.GetProperty(tool).GetString()!] = state.GetProperty(quantity).GetInt32();

        var ours = new HashSet<string>();
        if (step.EntryTool.TryGet(out var entryTool))
            ours.Add(piece.ItemNames[entryTool]);
        if (step.Material.TryGet(out var material))
        {
            var name = piece.ItemNames[material.Item];
            ours.Add(name);
            if (!entries.TryGetValue(name, out var count) || count != material.Count)
                return $"material {name}*{material.Count}, extract {Describe(entries)}";
        }

        if (!ours.SetEquals(entries.Keys))
            return $"entries {string.Join(",", ours)}, extract {Describe(entries)}";
        var exit = state.GetProperty("tool_exit");
        var theirExit = exit.ValueKind == JsonValueKind.String ? exit.GetString() : null;
        var ourExit = step.ExitTool.TryGet(out var exitTool) ? piece.ItemNames[exitTool] : null;
        return theirExit == ourExit ? null : $"exit {ourExit}, extract {theirExit}";
    }

    private static string Describe(Dictionary<string, int> entries) =>
        string.Join(",", entries.Select(e => $"{e.Key}*{e.Value}"));
}
