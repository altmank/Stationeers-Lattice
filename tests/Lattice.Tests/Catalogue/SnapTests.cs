using System.IO;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Core.Transforms;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.EntryPoint;
using Lattice.Tests.Runs;
using Lattice.Tests.Selectors;
using Lattice.Tests.Transforms;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// Each piece's snap (<see cref="PieceEntry.Snap"/>): the grid's own for most pieces, each axis its own for the game's
/// axis-independent pieces; what the catalogue reads and writes; and Lattice keeping to it: a ghost off its snap fails
/// <c>off-snap</c>, and runs and transforms that would put a piece there say so.
/// </summary>
public sealed class SnapTests
{
    /// <summary>An axis-independent small piece snapped as the game's engines and umbilicals are: x and z to frame centres.</summary>
    internal const string SnappedText = """
        lattice-catalogue 3
        piece ModThruster kit=ItemModThruster option=0 grid=small snap=2,0.5,2/0,0.25,0
          step material=ItemModThruster*1 exit=ItemWrench air=no
          end 0 0 0 z+ gas role=input
          cell 0 0 0 device
          collision 4

        piece ModRailLine kit=ItemModRail option=0 grid=small snap=2,0.5,2/0,0.25,0
          step material=ItemModRail*1 exit=ItemWireCutters air=no
          end 0 0 0 z+ power
          end 0 0 0 z- power
          cell 0 0 0 cable
          collision 2
        """;

    private static readonly PieceSnap Engine = new(new AxisSnap(2f, 0f), new AxisSnap(0.5f, 0.25f), new AxisSnap(2f, 0f));

    [Fact]
    public void The_grids_own_snap_holds_every_cell_centre_of_its_grid()
    {
        var small = PieceSnap.Of(PieceGrid.Small);
        var large = PieceSnap.Of(PieceGrid.Large);

        Assert.Equal(new AxisSnap(0.5f, 0.25f), small.X);
        Assert.Equal(new AxisSnap(2f, 0f), large.Y);
        foreach (var key in new[] { -15, -5, 0, 5, 105, 110, 115 })
            Assert.True(small.Holds(new Cell(key, key, key)), key.ToString());
        Assert.False(small.Holds(new Cell(102, 100, 100)));
        foreach (var key in new[] { -10, 10, 30, 110 })
            Assert.True(large.Holds(new Cell(key, key, key)), key.ToString());
        Assert.False(large.Holds(new Cell(100, 110, 110)));
        Assert.Equal(new Cell(110, 110, 110), Cell.FromWorld(new WorldPoint(11.2f, 10.6f, 11.9f), PieceGrid.Large));
        Assert.Equal(110, large.X.Key(11.2f));
    }

    [Fact]
    public void An_engines_snap_holds_x_and_z_on_frame_centres_only_and_y_on_every_half_metre()
    {
        Assert.True(Engine.Holds(new Cell(110, 115, 130)));
        Assert.Equal(Option.Some(Axis.X), Engine.OffAxis(new Cell(115, 110, 110)));
        Assert.Equal(Option.Some(Axis.Z), Engine.OffAxis(new Cell(110, 110, 100)));
        Assert.Equal(Option<Axis>.None, Engine.OffAxis(new Cell(-10, 5, 10)));
        Assert.True(default(AxisSnap).Holds(123), "a size of zero snaps nothing");
    }

    [Fact]
    public void An_entry_built_without_a_snap_has_its_grids_own()
    {
        var entry = new PieceEntry(new PrefabKey(1), "Piece", "Piece", PieceGrid.Large, ValueList<KitPlace>.Empty);

        Assert.Equal(PieceSnap.Of(PieceGrid.Large), entry.Snap);
        Assert.Equal(Engine, (entry with { Snap = Engine }).Snap);
    }

    [Fact]
    public void A_prefab_read_without_a_snap_maps_to_its_grids_own_and_with_one_to_its_own()
    {
        var plain = Prefabs.Ok(PieceMapper.Map(Prefabs.Piece("ModPlain"), ValueList<KitPlace>.Empty));
        var snapped = Prefabs.Ok(PieceMapper.Map(Prefabs.Piece("ModPlain") with { Snap = Option.Some(Engine) }, ValueList<KitPlace>.Empty));

        Assert.Equal(PieceSnap.Of(PieceGrid.Small), plain.Snap);
        Assert.Equal(Engine, snapped.Snap);
    }

    [Fact]
    public void The_data_file_writes_a_snap_only_where_it_is_not_the_grids_own_and_reads_it_back()
    {
        var plain = Prefabs.Piece("ModPlain");
        var thruster = Prefabs.Piece("ModThruster") with { Snap = Option.Some(Engine) };
        var frame = Prefabs.Piece("ModFrame", grid: 2.0) with { Snap = Option.Some(PieceSnap.Of(PieceGrid.Large)) };
        var catalogue = new PrefabCatalogue(new CountingSource([Prefabs.Kit("ItemKitMod", plain, thruster, frame)], [plain, thruster, frame]));
        var text = new StringWriter();

        CatalogueDataFile.Write(catalogue, catalogue.ReadAll(), System.TimeSpan.Zero, text);
        var written = text.ToString();
        var read = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(written), "snap.lcat"));

        Assert.Equal(1, written.Split(" snap=").Length - 1);
        Assert.Contains("piece ModThruster kit=ItemKitMod option=1 grid=small snap=2,0.5,2/0,0.25,0", written);
        Assert.Equal(Engine, read.Named("ModThruster").Snap);
        Assert.Equal(PieceSnap.Of(PieceGrid.Small), read.Named("ModPlain").Snap);
        Assert.Equal(PieceSnap.Of(PieceGrid.Large), read.Named("ModFrame").Snap);
    }

    [Fact]
    public void The_catalogue_view_gives_each_entry_its_pieces_snap()
    {
        var catalogue = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(SnappedText), "snapped.lcat"));
        var view = new PieceCatalogue(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue));

        Assert.Equal(Engine, Prefabs.Ok(view.Piece(catalogue.Named("ModThruster").Key)).Snap);
    }

    [Fact]
    public void A_ghost_off_its_pieces_snap_fails_off_snap_and_one_on_a_frame_centre_does_not()
    {
        using var bench = new WorldBench(SnappedText);
        var world = bench.Start();
        var off = Placed(bench, new Cell(115, 110, 110));
        var on = Placed(bench, new Cell(110, 115, 130));
        bench.Settle();

        var findings = Ready(world, off, on);

        var failed = Assert.IsType<Finding.WillFail>(findings.Find(f => f.Ghost == off));
        Assert.Equal(ReasonCodes.OffSnap, failed.Why.Code);
        Assert.Equal("115,110,110", failed.Why.Facts["cell"]);
        Assert.Equal("x", failed.Why.Facts["axis"]);
        Assert.Equal(bench.Runs.Catalogue.Named("ModThruster").Key.Hash.ToString(), failed.Why.Facts["piece"]);
        Assert.DoesNotContain(findings, f => f.Ghost == on && f is Finding.WillFail);
        Assert.Equal(new Cell(115, 110, 110), Built(world, off).At);
    }

    [Fact]
    public void A_move_that_takes_a_snapped_piece_off_a_frame_centre_is_refused_off_snap_and_a_whole_frame_step_is_not()
    {
        var b = new TransformBench(SnappedText);
        var thruster = b.Build("ModThruster", new Cell(110, 110, 110));
        var before = b.Placed();

        var refused = Lattice.Tests.Board.TestBoard.Refused(b.Transform(TransformBench.All, new Transformation.Move(new CellOffset(5, 0, 0))));
        Assert.Equal(ReasonCodes.OffSnap, refused.Why.Code);
        Assert.Equal(new[] { thruster }, refused.Ghosts);
        Assert.Equal(b.T.Catalogue.Named("ModThruster").Key.Hash.ToString(), refused.Why.Facts["pieces"]);
        Assert.Equal(before, b.Placed());

        Assert.IsType<Outcome.Applied>(b.Transform(TransformBench.All, new Transformation.Move(new CellOffset(20, 5, 0))));
        Assert.True(b.Placements.Apply(new Transformation.Move(new CellOffset(20, 5, 0)),
            new Placement(b.T.Catalogue.Named("ModThruster").Key, new Cell(110, 110, 110), default)).IsOk);
        Assert.Equal(TransformProblem.OffSnap, b.Placements.Apply(new Transformation.Move(new CellOffset(0, 0, 5)),
            new Placement(b.T.Catalogue.Named("ModThruster").Key, new Cell(110, 110, 110), default)).ErrorOf());
    }

    [Fact]
    public void A_run_that_lays_a_snapped_piece_off_its_snap_reports_off_snap_at_its_first_such_cell()
    {
        var bench = new RunBench(SnappedText);
        var rail = bench.KitOf("ModRailLine");

        var across = bench.Plan(bench.Run(rail, new Cell(110, 110, 110), new Cell(150, 110, 110)));
        var along = bench.Plan(bench.Run(rail, new Cell(110, 110, 110), new Cell(110, 150, 110)));

        Assert.True(across.FirstFailure.TryGet(out var failure));
        Assert.Equal(new Cell(115, 110, 110), failure.At);
        Assert.Equal(ReasonCodes.OffSnap, failure.Why.Code);
        Assert.Equal("x", failure.Why.Facts["axis"]);
        Assert.False(along.FirstFailure.IsSome);
    }

    [GameCatalogueFact]
    public void The_games_gas_and_liquid_engines_and_umbilicals_snap_x_and_z_to_frame_centres_and_ordinary_pieces_to_their_grid()
    {
        var catalogue = GameCatalogue.Load();
        var view = new PieceCatalogue(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue));
        PieceSnap SnapOf(string piece) => Prefabs.Ok(view.Piece(PrefabHash.Piece(piece))).Snap;

        foreach (var piece in new[] { "StructurePressureFedGasEngine", "StructurePumpedLiquidEngine", "StructureGovernedGasEngine", "StructureGasUmbilicalMale", "StructurePowerUmbilicalMale" })
            Assert.Equal(Engine, SnapOf(piece));
        Assert.Equal(Engine, catalogue.Named("StructurePressureFedGasEngine").Snap);
        var crew = new AxisSnap(2f, 0f);
        Assert.Equal(new PieceSnap(crew, new AxisSnap(2f, 2f), crew), SnapOf("StructureCrewUmbilical"));
        Assert.Equal(PieceSnap.Of(PieceGrid.Small), SnapOf("StructureRocketEngineSmall"));
        Assert.Equal(PieceSnap.Of(PieceGrid.Small), SnapOf("StructureCableStraight"));
        Assert.Equal(PieceSnap.Of(PieceGrid.Large), SnapOf("StructureFrame"));
        var own = 0;
        foreach (var entry in view.Pieces)
            if (entry.Snap != PieceSnap.Of(entry.Grid))
                own++;
        Assert.Equal(13, own);
    }

    private static GhostId Placed(WorldBench bench, Cell at) =>
        Assert.IsType<Outcome.Applied>(bench.Submit(new Command.Place(ValueList.Of(
            Lattice.Tests.Board.TestBoard.Draft(new Intent.Build(new Placement(bench.Runs.Catalogue.Named("ModThruster").Key, at, default), Option<Finish>.None))))))
            .Changes.Added[0].Id;

    private static System.Collections.Generic.List<Finding> Ready(Lattice.Core.EntryPoint.LatticeWorld world, params GhostId[] ghosts)
    {
        var report = world.Validation.Check(new Selector.Ids(ValueList.From(ghosts))).Match(static a => a, static why => throw new Xunit.Sdk.XunitException(why.ToString()));
        return new System.Collections.Generic.List<Finding>(Assert.IsType<Answer<ValidationReport>.Ready>(report).Value.Findings);
    }

    private static Placement Built(Lattice.Core.EntryPoint.LatticeWorld world, GhostId ghost) =>
        ((Intent.Build)world.Ghosts.Index.Ghosts[ghost].Ghost.Intent).Target;
}
