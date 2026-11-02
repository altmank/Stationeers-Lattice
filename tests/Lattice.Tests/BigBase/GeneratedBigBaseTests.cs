using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.TestKit;
using Lattice.BigBase;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.Fixtures;
using Lattice.Tests.Catalogue;
using Xunit.Abstractions;

namespace Lattice.Tests.BigBase;

/// <summary>
/// A fact over the generated big base: the game's catalogue, the base file and its mix in tests/fixtures/generated
/// (copied from LatticeFixtures\bigbase by make-fixtures.ps1). Missing ones skip or fail it as any
/// <see cref="FixtureFactAttribute"/>.
/// </summary>
public sealed class BigBaseFactAttribute() : FixtureFactAttribute(Lacking())
{
    private static string? Lacking() =>
        GameCatalogue.Path().IsNone
            ? "No catalogue written by the game in tests/fixtures/generated: run tests/fixtures/make-fixtures.ps1 -Catalogue <file>."
            : MissingSave([GeneratedBigBaseTests.BaseFileName, GeneratedBigBaseTests.MixFileName]);
}

/// <summary>
/// The generated big base (LatticeFixtures\bigbase\lattice-gen-big.tsv, written by tests/Lattice.BigBase from the
/// game's catalogue and lattice-gen-big-mix.txt): its counts per piece group sit within 10% of the mix, and every
/// piece stands where and how the game builds it.
/// </summary>
public sealed class GeneratedBigBaseTests(ITestOutputHelper output)
{
    public const string BaseFileName = "lattice-gen-big.tsv";
    public const string MixFileName = "lattice-gen-big-mix.txt";

    [BigBaseFact]
    public void Counts_per_piece_group_sit_within_ten_percent_of_the_target_mix()
    {
        var (catalogue, mix, list) = Load();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var piece in list.Pieces)
        {
            var family = catalogue.Piece(PrefabHash.Piece(piece.Prefab)).TryGet(out var facts) ? facts.Family.Value : "unknown";
            counts[family] = counts.GetValueOrDefault(family) + 1;
        }

        foreach (var (family, target) in mix.Groups)
            output.WriteLine($"{family}: {counts.GetValueOrDefault(family)} of a target {target}");
        Assert.All(mix.Groups, g => Assert.InRange(counts.GetValueOrDefault(g.Key), g.Value * 0.9, g.Value * 1.1));
        Assert.Equal(0, counts.Keys.Count(k => !mix.Groups.TryGetValue(k, out _)));
    }

    [BigBaseFact]
    public void Every_piece_stands_on_its_grids_snap_in_a_turn_its_kind_allows()
    {
        var (catalogue, _, list) = Load();
        var problems = new List<string>();
        foreach (var piece in list.Pieces)
        {
            if (!catalogue.Piece(PrefabHash.Piece(piece.Prefab)).TryGet(out var facts))
                problems.Add($"{piece.Prefab} is not in the catalogue");
            else if (!facts.AllowedTurns.Contains(piece.Turn))
                problems.Add($"{piece.Prefab} at {piece.At} in turn {piece.Turn.Index}, allowed {facts.AllowedTurns}");
            else if (!facts.Snap.Holds(piece.At))
                problems.Add($"{piece.Prefab} at {piece.At} is off its snap");
        }

        Assert.True(list.Pieces.Count > 30000, $"only {list.Pieces.Count} pieces");
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
    }

    // One piece a spot, as the game keys them; a face piece on exactly one face; small pieces in one cell neither of a
    // clashing class nor with ends that collide.
    [BigBaseFact]
    public void No_two_pieces_take_one_spot_or_clash_in_a_small_cell()
    {
        var (catalogue, _, list) = Load();
        var shapes = new Shapes(catalogue, new CubeGrid());
        var taken = new Dictionary<SpotKey, string>();
        var small = new Dictionary<Cell, List<PlacedShape>>();
        var problems = new List<string>();
        foreach (var piece in list.Pieces)
        {
            var placement = new Placement(PrefabHash.Piece(piece.Prefab), piece.At, piece.Turn);
            if (!shapes.Of(placement).TryGet(out var shape))
            {
                problems.Add($"{piece.Prefab} has no shape");
                continue;
            }

            if (shape.Piece.Face.IsSome && shape.Spots.Count != 1)
                problems.Add($"{piece.Prefab} at {piece.At} takes {shape.Spots.Count} faces");
            foreach (var spot in shape.Spots)
            {
                if (!taken.TryAdd(spot, piece.Prefab))
                    problems.Add($"{piece.Prefab} at {piece.At} takes {spot}, which {taken[spot]} takes");
                if (!shape.IsSmall)
                    continue;
                if (!small.TryGetValue(spot.Cell, out var here))
                    small[spot.Cell] = here = [];
                foreach (var other in here)
                    if (Shapes.Conflict(shape, other).TryGet(out var why))
                        problems.Add($"{piece.Prefab} at {piece.At} clashes with {catalogue.NameOf(other.Piece.Key)}: {why}");
                here.Add(shape);
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
    }

    // The pipe networks (one per stack of corridors), the one power grid and each room's chutes are each laid as one
    // connected piece, read back from the pieces alone; no run cell is dropped for want of a kit piece; and every gas,
    // power and chute end of a device reaches a run of its kind.
    [BigBaseFact]
    public void Each_network_is_one_connected_piece_and_device_ends_are_joined()
    {
        var (catalogue, mix, list) = Load();
        var made = new BaseGenerator(catalogue, mix, 128).Generate();
        Assert.True(made.TryGet(out var generated), "the generator refused the catalogue or mix");
        var report = NetworkCheck.Of(catalogue, list.Pieces);

        foreach (var (family, count) in generated.Networks)
            output.WriteLine($"{family}: {report.Components.GetValueOrDefault(family)} connected pieces of {count} networks");
        output.WriteLine($"device ends: {report.UnjoinedEnds} of {report.DeviceEnds} reach no run");
        foreach (var example in report.Unjoined.Take(10))
            output.WriteLine("unjoined: " + example);
        Assert.Equal(0, generated.DroppedRuns);
        Assert.Equal(generated.Networks.OrderBy(n => n.Key), report.Components.OrderBy(n => n.Key));
        Assert.Equal(0, report.UnjoinedEnds);
    }

    [BigBaseFact]
    public void The_base_stands_on_several_storeys_with_a_real_bases_spread_of_heights()
    {
        var (_, _, list) = Load();

        var heights = list.Pieces.Select(p => p.At.Y).Distinct().Count();

        output.WriteLine($"{heights} distinct heights");
        Assert.True(heights >= 54, $"only {heights} distinct heights");
    }

    // A mix or catalogue short of a family leaves it out; it never takes the generator down.
    [BigBaseFact]
    public void A_mix_missing_a_family_still_makes_a_base_without_that_family()
    {
        var (catalogue, full, _) = Load();
        foreach (var family in full.Groups.Keys)
        {
            var mix = new BaseMix(
                ValueMap.From(full.Groups.Where(g => g.Key != family).ToDictionary(g => g.Key, g => g.Value / 10)),
                ValueMap.From(full.Pieces.Where(g => g.Key != family).ToDictionary(g => g.Key, g => g.Value)),
                ValueMap.From(full.Kits.Where(g => g.Key != family).ToDictionary(g => g.Key, g => g.Value)),
                full.OnPipe);

            var made = new BaseGenerator(catalogue, mix, 7).Generate();

            Assert.True(made.TryGet(out var generated), $"no base without {family}: " + made.Match(static _ => "", static why => why));
            Assert.Equal(0, generated.Counts.GetValueOrDefault(family));
            Assert.Equal(0, generated.DroppedRuns);
        }
    }

    [BigBaseFact]
    public void The_file_names_the_save_the_live_build_makes()
    {
        var (_, _, list) = Load();

        Assert.Equal(Option.Some(BigBaseTool.Station), list.Station);
    }

    [BigBaseFact]
    public void The_same_seed_makes_the_same_base_and_another_seed_another()
    {
        var catalogue = GameCatalogue.Path().Reduce("");
        var mix = Fixtures.Save(MixFileName).Reduce("");

        var first = TestKitIds.MustRead(BigBaseTool.Make(catalogue, mix, 128).MapError(e => new FileError(mix, 0, e)));
        var again = TestKitIds.MustRead(BigBaseTool.Make(catalogue, mix, 128).MapError(e => new FileError(mix, 0, e)));
        var other = TestKitIds.MustRead(BigBaseTool.Make(catalogue, mix, 129).MapError(e => new FileError(mix, 0, e)));

        Assert.Equal(first.Text, again.Text);
        Assert.NotEqual(first.Text, other.Text);
    }

    [Fact]
    public void Every_turn_written_as_a_quaternion_reads_back_as_itself()
    {
        var pieces = Enumerable.Range(0, Turn.Count)
            .Select(i => new BasePiece("Piece", new Cell(10 * i + 5, -35, 6010), TestKitIds.Must(Turn.Create(i)), i % 3))
            .ToList();
        using var text = new StringWriter();
        BaseFile.Write(text, ["station test"], pieces);

        var read = TestKitIds.MustRead(BaseFile.Read(new StringReader(text.ToString()), "written"));

        Assert.Equal<BasePiece>(pieces, read.Pieces);
        Assert.Equal(Option.Some("test"), read.Station);
    }

    [Fact]
    public void A_mix_file_reads_its_groups_pieces_kits_and_on_pipe_share()
    {
        var mix = TestKitIds.MustRead(BaseMix.Read(new StringReader(
            "# a comment\nstructures 30\ngroup frame 10\ngroup pipe 20\npiece frame A 3\nkit pipe K 1\non-pipe R 30\n"), "mix"));

        Assert.Equal(10, mix.Target(PieceFamilies.Frame));
        Assert.Equal(0, mix.Target(PieceFamilies.Wall));
        Assert.Equal<Weighted>([new Weighted("A", 3)], BaseMix.Of(mix.Pieces, PieceFamilies.Frame));
        Assert.Equal<Weighted>([new Weighted("K", 1)], BaseMix.Of(mix.Kits, PieceFamilies.Pipe));
        Assert.Equal<Weighted>([new Weighted("R", 30)], mix.OnPipe);
        Assert.True(BaseMix.Read(new StringReader("group frame many\n"), "bad").IsError);
    }

    private static (FakeCatalogue Catalogue, BaseMix Mix, BaseList List) Load() =>
        (GameCatalogue.Load(),
         TestKitIds.MustRead(BaseMix.Load(Fixtures.Save(MixFileName).Reduce(""))),
         TestKitIds.MustRead(BaseFile.Load(Fixtures.Save(BaseFileName).Reduce(""))));
}
