using System;
using System.IO;
using System.Linq;
using Lattice.TestKit.Fixtures;

namespace Lattice.Tests.EntryPoint;

/// <summary>
/// What the cells live run recorded on the test instance (Lattice.Game.Live <c>cells</c>; log copied from
/// <c>LatticeFixturesridge</c> by make-fixtures.ps1): <c>Cell.FromWorld</c> keys every point on both grids as the
/// game's own <c>Grid3(GridCenter(GridSize, GridOffset))</c> does, for the grids the structure prefabs declare.
/// </summary>
public sealed class LiveCellsTests
{
    private const string Log = "live-bridge-cells.log";

    [SaveFact(Log)]
    public void Cell_from_world_keys_every_point_as_the_game_snaps_it_on_both_grids()
    {
        var log = File.ReadAllLines(Path.Combine(Fixtures.Generated, Log));

        Assert.DoesNotContain(log, static l => l.StartsWith("LIVE FAIL", StringComparison.Ordinal));
        Assert.Contains("LIVE PASS Cell.FromWorld keys every point as the game snaps it", log);
        Assert.DoesNotContain(log, static l => l.StartsWith("LIVE cells differ", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE cells grid 2 offset 0:", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE cells grid 0.5 offset 0.25:", StringComparison.Ordinal));
    }
}
