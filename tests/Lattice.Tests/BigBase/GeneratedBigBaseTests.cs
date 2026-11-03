using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.Catalogue;
using Lattice.Core.Support;
using Lattice.Core.Validation;
using Lattice.TestKit.Scenarios;
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
        var families = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var piece in list.Pieces)
        {
            var known = catalogue.Piece(PrefabHash.Piece(piece.Prefab)).TryGet(out var facts);
            var family = known ? facts!.Family.Value : "unknown";
            counts[family] = counts.GetValueOrDefault(family) + 1;
            families[family] = counts[family];
            if (known && MountedKinds.IsVent(facts!))
                counts[MountedKinds.Vent] = counts.GetValueOrDefault(MountedKinds.Vent) + 1;
            if (known && MountedKinds.IsControl(facts!))
                counts[MountedKinds.Control] = counts.GetValueOrDefault(MountedKinds.Control) + 1;
        }

        foreach (var (family, target) in mix.Groups)
            output.WriteLine($"{family}: {counts.GetValueOrDefault(family)} of a target {target}");
        Assert.All(mix.Groups, g => Assert.InRange(counts.GetValueOrDefault(g.Key), g.Value * 0.9, g.Value * 1.1));
        Assert.Equal(0, families.Keys.Count(k => !mix.Groups.TryGetValue(k, out _)));
    }

    // The windows the perf suites plan from (Take(5000), Skip(5000).Take(5000), Skip(15000).Take(500)) read pieces in build
    // order, so every stretch of 5,000 rows holds the groups a plan is about. Devices are 2% of the base, so they get 1%.
    [BigBaseFact]
    public void Every_five_thousand_row_window_mixes_the_groups()
    {
        var (catalogue, _, list) = Load();
        const int window = 5000;
        var groups = new[] { "frame", "wall", "pipe", "cable", "device", MountedKinds.Vent, MountedKinds.Control };
        var floors = new Dictionary<string, int>
        {
            ["frame"] = window * 3 / 100, ["wall"] = window * 3 / 100, ["pipe"] = window * 3 / 100,
            ["cable"] = window * 3 / 100, ["device"] = window / 100, [MountedKinds.Vent] = 1, [MountedKinds.Control] = 1,
        };
        var inside = groups.ToDictionary(g => g, _ => 0);
        var least = groups.ToDictionary(g => g, _ => int.MaxValue);
        var rows = list.Pieces.Select(p => Facts(catalogue, p.Prefab)).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            foreach (var g in GroupsOf(rows[i]))
                inside[g]++;
            if (i >= window)
                foreach (var g in GroupsOf(rows[i - window]))
                    inside[g]--;
            if (i >= window - 1)
                foreach (var g in groups)
                    least[g] = Math.Min(least[g], inside[g]);
        }

        foreach (var g in groups)
            output.WriteLine($"{g}: at least {least[g]} in any {window} rows (needs {floors[g]})");
        Assert.All(groups, g => Assert.True(least[g] >= floors[g], $"{g}: only {least[g]} in a {window}-row window, needs {floors[g]}"));

        static IEnumerable<string> GroupsOf(PieceFacts facts)
        {
            if (facts.Family == PieceFamilies.Frame) yield return "frame";
            if (facts.Family == PieceFamilies.Wall) yield return "wall";
            if (facts.Family == PieceFamilies.Pipe) yield return "pipe";
            if (facts.Family == PieceFamilies.Cable) yield return "cable";
            if (facts.Family == PieceFamilies.Device) yield return "device";
            if (MountedKinds.IsVent(facts)) yield return MountedKinds.Vent;
            if (MountedKinds.IsControl(facts)) yield return MountedKinds.Control;
        }
    }

    [BigBaseFact]
    public void Vents_and_controls_are_placed_and_joined()
    {
        var (catalogue, mix, list) = Load();
        var report = NetworkCheck.Of(catalogue, list.Pieces);
        var vents = list.Pieces.Where(p => MountedKinds.IsVent(Facts(catalogue, p.Prefab))).ToList();
        var controls = list.Pieces.Where(p => MountedKinds.IsControl(Facts(catalogue, p.Prefab))).ToList();

        output.WriteLine($"{vents.Count} vents, {controls.Count} controls; {report.UnjoinedEnds} of {report.DeviceEnds} device ends reach no run");
        Assert.InRange(vents.Count, mix.Groups[MountedKinds.Vent] * 0.9, mix.Groups[MountedKinds.Vent] * 1.1);
        Assert.InRange(controls.Count, mix.Groups[MountedKinds.Control] * 0.9, mix.Groups[MountedKinds.Control] * 1.1);
        Assert.Equal(0, report.UnjoinedEnds);
        var mounted = vents.Concat(controls).Select(p => p.Prefab).ToHashSet();
        Assert.DoesNotContain(report.Unjoined, u => mounted.Any(m => u.StartsWith(m + " ", StringComparison.Ordinal)));
    }

    [BigBaseFact]
    public void The_stand_spot_is_a_free_floor_cell()
    {
        var (catalogue, _, list) = Load();
        Assert.True(list.Stand.TryGet(out var stand), "the base file has no stand line");
        var (column, row) = (Math.DivRem(stand.X, 20, out _), Math.DivRem(stand.Z, 20, out _));
        var floorTop = stand.Y - 10;

        var frameBelow = list.Pieces.Any(p => p.At == new Cell(20 * column + 10, floorTop - 10, 20 * row + 10) &&
            Facts(catalogue, p.Prefab).Family == PieceFamilies.Frame);
        var standing = list.Pieces.Where(p =>
            p.At.Y >= floorTop && p.At.Y < floorTop + 40 &&
            p.At.X >= 20 * column && p.At.X <= 20 * column + 20 && p.At.Z >= 20 * row && p.At.Z <= 20 * row + 20 &&
            Facts(catalogue, p.Prefab).Family is var f &&
            f != PieceFamilies.Frame && f != PieceFamilies.Wall && f != PieceFamilies.Window).ToList();

        output.WriteLine($"stand {stand}, floor top {floorTop}");
        Assert.True(frameBelow, "no floor frame under the stand spot");
        Assert.Empty(standing);
        Assert.InRange(stand.X, list.Pieces.Min(p => p.At.X), list.Pieces.Max(p => p.At.X));
        Assert.InRange(stand.Y, list.Pieces.Min(p => p.At.Y), list.Pieces.Max(p => p.At.Y));
        Assert.InRange(stand.Z, list.Pieces.Min(p => p.At.Z), list.Pieces.Max(p => p.At.Z));
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

    // A mix without vents or controls leaves both out and still makes the base.
    [BigBaseFact]
    public void A_mix_without_vents_and_controls_makes_a_base_with_none_placed()
    {
        var (catalogue, full, _) = Load();
        var mix = new BaseMix(
            ValueMap.From(full.Groups.Where(g => g.Key is not (MountedKinds.Vent or MountedKinds.Control)).ToDictionary(g => g.Key, g => g.Value / 10)),
            full.Pieces,
            full.Kits,
            full.OnPipe);

        var made = new BaseGenerator(catalogue, mix, 7).Generate();

        Assert.True(made.TryGet(out var generated), "no base without vents and controls: " + made.Match(static _ => "", static why => why));
        Assert.Equal(0, generated.Counts.GetValueOrDefault(MountedKinds.Vent));
        Assert.Equal(0, generated.Counts.GetValueOrDefault(MountedKinds.Control));
        Assert.DoesNotContain(generated.Pieces, p => MountedKinds.IsVent(Facts(catalogue, p.Prefab)) || MountedKinds.IsControl(Facts(catalogue, p.Prefab)));
        Assert.True(generated.Pieces.Count > 3000, $"only {generated.Pieces.Count} pieces");
    }

    // The game refuses a face-mounted piece that lies on no supporting face (CanMountOnWall); Lattice's own wall-mount
    // rule is run over every small face-mounted piece in the file, against the other pieces standing.
    [BigBaseFact]
    public void Every_face_mounted_piece_passes_the_wall_mount_support_rule()
    {
        var (catalogue, _, list) = Load();
        var scenario = Scenario.With(catalogue);
        foreach (var piece in list.Pieces)
            scenario.Built(piece.Prefab, piece.At, piece.Turn.Index);
        var built = scenario.Build();
        var grid = new CubeGrid();
        var geometry = new SupportGeometry(grid, new FamilySupportFacts(catalogue), catalogue);
        var rule = new WallMountRule(geometry);
        var facts = new SpotFacts(new DirectFacts(built.World, GameChecks.None), built.World.All, new Shapes(catalogue, grid));
        var world = new SupportWorld(new AssumedIndex(), AssumedIndex.Every, false, facts, catalogue, new ReadLog());

        var checkedPieces = 0;
        var problems = new List<string>();
        foreach (var piece in list.Pieces)
        {
            var pieceFacts = Facts(catalogue, piece.Prefab);
            if (pieceFacts.Grid != GridSize.Small || pieceFacts.Turning.Placement != PlacedBy.FaceMount)
                continue;
            checkedPieces++;
            var at = new Placement(pieceFacts.Key, piece.At, piece.Turn);
            var judged = rule.Judge(new SupportCase(new GhostId(1), at, pieceFacts, world));
            if (judged is not Judgement.Supported)
                problems.Add($"{piece.Prefab} at {piece.At} turn {piece.Turn.Index}: {(judged is Judgement.Unsupported u ? u.Why.Facts.GetValueOrDefault("game") ?? "" : judged.GetType().Name)}");
        }

        output.WriteLine($"{checkedPieces} face-mounted small pieces, {problems.Count} not supported");
        foreach (var problem in problems.Take(30))
            output.WriteLine(problem);
        Assert.True(checkedPieces > 0, "no face-mounted small piece in the file");
        Assert.True(problems.Count == 0, $"{problems.Count} of {checkedPieces} face-mounted pieces fail:\n" + string.Join("\n", problems.Take(20)));
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

    // The world as the game's grid reports it: a structure listed in every cell whose spot it takes, so a wall is found
    // from the large cells on both sides of it. The test kit's world lists a structure at its own position only.
    private sealed class SpotFacts : IJudgeFacts
    {
        private readonly IJudgeFacts _inner;
        private readonly Dictionary<Cell, List<StructureInfo>> _byCell = [];

        public SpotFacts(IJudgeFacts inner, IEnumerable<StructureInfo> structures, Shapes shapes)
        {
            _inner = inner;
            foreach (var info in structures)
            {
                var cells = new HashSet<Cell> { info.Ref.At };
                if (shapes.Of(new Placement(info.Ref.Piece, info.Ref.At, info.Turn)).TryGet(out var shape))
                    foreach (var spot in shape.Spots)
                        cells.Add(spot.Cell);
                foreach (var cell in cells)
                {
                    if (!_byCell.TryGetValue(cell, out var here))
                        _byCell[cell] = here = [];
                    here.Add(info);
                }
            }
        }

        public Option<StructureInfo> Structure(ReferenceId id) => _inner.Structure(id);

        public ValueList<StructureInfo> At(Cell cell) =>
            _byCell.TryGetValue(cell, out var here) ? ValueList.From(here) : ValueList<StructureInfo>.Empty;

        public Option<Option<NetworkRef>> NetworkOf(ReferenceId id) => _inner.NetworkOf(id);

        public Option<Option<RoomRef>> RoomAt(Cell cell) => _inner.RoomAt(cell);

        public Option<GameAnswer> Game(Placement placement) => _inner.Game(placement);

        public Option<Guarded<SupportVerdict>> MainSupport(SupportRules rules, SupportRules.Entry entry, GhostId ghost, SupportInput input) =>
            _inner.MainSupport(rules, entry, ghost, input);

        public Option<Guarded<RuleVerdict>> MainRule(ValidationRules rules, ValidationRules.Entry entry, RuleInput input) =>
            _inner.MainRule(rules, entry, input);

        public int EpochOf(GhostId ghost) => _inner.EpochOf(ghost);
    }

    // The catalogue file holds no support facts (the game's SmallGrid.AllowMounting and BlockGrid), so the rule would leave
    // every piece to the game. Stand-ins by family: frames allow mounting and block their cell, walls allow mounting,
    // windows offer nothing, and a small face-mounted piece blocks no cell and offers no mounting.
    private sealed class FamilySupportFacts(FakeCatalogue catalogue) : ISupportFacts
    {
        public Option<SupportFacts> Of(PrefabKey piece)
        {
            if (!catalogue.Piece(piece).TryGet(out var facts))
                return Option<SupportFacts>.None;
            if (facts.Family == PieceFamilies.Frame)
                return Option.Some(new SupportFacts(true, true, false, ValueSet<HostRole>.Empty));
            if (facts.Family == PieceFamilies.Wall)
                return Option.Some(new SupportFacts(true, false, false, ValueSet<HostRole>.Empty));
            return facts.Family == PieceFamilies.Window || (facts.Grid == GridSize.Small && facts.Turning.Placement == PlacedBy.FaceMount)
                ? Option.Some(SupportFacts.Nothing)
                : Option<SupportFacts>.None;
        }
    }

    private static PieceFacts Facts(FakeCatalogue catalogue, string prefab) => TestKitIds.Must(catalogue.Piece(PrefabHash.Piece(prefab)));

    private static (FakeCatalogue Catalogue, BaseMix Mix, BaseList List) Load() =>
        (GameCatalogue.Load(),
         TestKitIds.MustRead(BaseMix.Load(Fixtures.Save(MixFileName).Reduce(""))),
         TestKitIds.MustRead(BaseFile.Load(Fixtures.Save(BaseFileName).Reduce(""))));
}
