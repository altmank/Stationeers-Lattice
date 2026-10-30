using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Openings;
using Lattice.Core.Routes;
using Lattice.Core.Runs;
using Lattice.Testing.Stubs;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.PlacementCheck;
using Lattice.TestKit.World;
using Lattice.Tests.Board;
using Lattice.Tests.Catalogue;
using Lattice.Tests.Routes;
using Xunit;

namespace Lattice.Tests.Runs;

/// <summary>
/// Cable runs and routes past built devices of the game's own catalogue (the fixture export): every cell a small
/// device fills is taken by the small-cell clash rule, so a drawn run through one fails there and a route goes round.
/// A large-grid device takes no small cell: the game's small-piece check reads small cells only (decompile
/// Assets.Scripts.Objects/SmallGrid.cs, CanConstruct), so a cable through its large cell lays, as in the game.
/// </summary>
public sealed class RealDeviceRunTests
{
    private const long Built = 7001;
    private static readonly string[] Batteries = ["StructureBattery", "StructureBatteryLarge"];
    private static readonly Cell Origin = new(110, 110, 110);
    private static readonly RouteToggles Off = new(false, false, false, false, false);

    private static Cell At(int x, int y, int z) => new(Origin.X + 5 * x, Origin.Y + 5 * y, Origin.Z + 5 * z);

    private sealed class Bench
    {
        public Bench(string device)
        {
            Catalogue = GameCatalogue.Load();
            World = new FakeWorld(Catalogue);
            Device = Catalogue.Named(device).Key;
            World.Load(new StructureInfo(new StructureRef(new ReferenceId(Built), Device, Origin), default, new BuildState.Built(),
                Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None));
            Cable = Catalogue.Named("StructureCableStraight").Kits[0].Kit;
            Cells = TestKitIds.Must(new Shapes(Catalogue, Grid).Of(new Placement(Device, Origin, default))).Spots
                .Select(static spot => spot.Cell).Distinct().ToList();
        }

        public FakeCatalogue Catalogue { get; }

        public FakeWorld World { get; }

        public PrefabKey Device { get; }

        public KitKey Cable { get; }

        public CubeGrid Grid { get; } = new();

        public FakePlacementCheck Check { get; } = new();

        public IReadOnlyList<Cell> Cells { get; }

        public RunPlan Run(Cell from, Cell to) => TestKitIds.Must(new RunPlanner(Catalogue, Grid, Check).Plan(
            new RunRequest(Cable, from, ValueList<Cell>.Empty, to, Option<PlanId>.None, TestBoard.TestMaker), new NoGhosts(), new FakeWorldQuery(World)));

        public RoutePlan Route(Cell from, Cell to) => TestKitIds.Must(
            new RoutePlanner(Catalogue, Grid, Check, new TestOpeningFacts(), new TestFrames(), OpeningBands.Default, RouteLimits.Default)
                .Plan(new RouteRequest(Cable, from, to, Off, Option<PlanId>.None, TestBoard.TestMaker), new NoGhosts(), new FakeWorldQuery(World)));
    }

    [GameCatalogueFact]
    public void A_cable_run_through_any_cell_of_a_built_battery_fails_cannot_share_there_naming_it()
    {
        foreach (var battery in Batteries)
        {
            var bench = new Bench(battery);
            Assert.True(bench.Cells.Count >= 8, battery);

            foreach (var cell in bench.Cells)
            {
                var plan = bench.Run(cell with { Z = cell.Z - 15 }, cell with { Z = cell.Z + 15 });

                Assert.True(plan.FirstFailure.TryGet(out var failure), $"a run through {battery}'s cell {cell} fits");
                Assert.Contains(failure.At, bench.Cells);
                Assert.Equal(ReasonCodes.CannotShare, failure.Why.Code);
                Assert.Equal(bench.Device.Hash.ToString(), failure.Why.Facts["with"]);
                Assert.Equal(Built.ToString(), failure.Why.Facts["structure"]);
            }
        }
    }

    [GameCatalogueFact]
    public void A_cable_run_one_cell_past_a_built_battery_lays()
    {
        foreach (var battery in Batteries)
        {
            var bench = new Bench(battery);
            var beside = bench.Cells.Min(static cell => cell.X) - 5;

            var plan = bench.Run(new Cell(beside, Origin.Y, Origin.Z - 15), new Cell(beside, Origin.Y, Origin.Z + 15));

            Assert.True(plan.FirstFailure.IsNone, battery);
            Assert.Equal(7, plan.Cells.Count);
        }
    }

    [GameCatalogueFact]
    public void A_cable_route_between_the_same_ends_goes_round_a_built_battery()
    {
        foreach (var battery in Batteries)
        {
            var bench = new Bench(battery);

            var plan = bench.Route(At(0, 0, -3), At(0, 0, 3));

            Assert.True(plan.Run.FirstFailure.IsNone, battery);
            Assert.NotEqual(0, plan.Bends);
            Assert.DoesNotContain(plan.Run.Cells, cell => bench.Cells.Contains(cell));
        }
    }

    [GameCatalogueFact]
    public void A_cable_run_through_a_built_large_grid_devices_cell_lays_as_in_the_game()
    {
        var bench = new Bench("StructureAutoMinerSmall");

        var plan = bench.Run(At(0, 0, -3), At(0, 0, 3));

        Assert.True(plan.FirstFailure.IsNone);
    }

    private sealed class NoGhosts : StubGhostBoard
    {
        public override Revision Revision => default;

        public override Option<Ghost> Find(GhostId id) => Option<Ghost>.None;

        public override ValueList<Ghost> At(Cell cell) => ValueList<Ghost>.Empty;

        public override ValueList<Ghost> On(StructureRef structure) => ValueList<Ghost>.Empty;
    }
}
