using System;
using System.IO;
using System.Linq;
using Lattice.TestKit.Fixtures;

namespace Lattice.Tests.World;

/// <summary>
/// Build safety's game facts read back in a running world on the test instance (Lattice.Game.Live mode <c>safety</c>,
/// vanilla with MatterLink; log copied by make-fixtures.ps1): the catalogue's safety facts at world load, a pipe
/// network's temperature against the game's own number, and terrain in large cells. The headless server forms no room
/// around spawned frames, so the room temperature is not read there.
/// </summary>
public sealed class LiveSafetyTests
{
    private const string Run = "live-bridge-safety.log";

    [SaveFact(Run)]
    public void The_safety_run_finished_with_no_failure()
    {
        var log = Lines();
        Assert.DoesNotContain(log, l => l.Contains(" FAIL "));
        Assert.Contains("LIVE done", log);
    }

    [SaveFact(Run)]
    public void The_catalogue_at_world_load_carries_the_safety_facts_by_type_a_modded_piece_included()
    {
        var log = Lines();
        Assert.Contains("LIVE safety piece StructureActiveVent: keeps=air supplies=no control=y+ vents=no", log);
        Assert.Contains("LIVE safety piece StructureWallHeater: keeps=heat supplies=no control=z+ vents=no", log);
        Assert.Contains("LIVE safety piece StructureBattery: keeps=none supplies=yes control=x+ vents=no", log);
        Assert.Contains("LIVE safety piece StructurePassiveVent: keeps=none supplies=no control=none vents=yes", log);
        Assert.Contains("LIVE safety piece StructureFrame: keeps=none supplies=no control=none vents=no", log);
        Assert.Contains(log, l => l.StartsWith("LIVE safety modded piece StructureMatterlink", StringComparison.Ordinal));
    }

    [SaveFact(Run)]
    public void A_pipe_networks_temperature_and_terrain_in_a_cell_are_the_games()
    {
        var log = Lines();
        Assert.Contains(log, l => l.StartsWith("LIVE PASS safety network temperature", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("LIVE PASS safety terrain in the surface cell", StringComparison.Ordinal) && l.EndsWith("True", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("LIVE PASS safety terrain twenty metres up", StringComparison.Ordinal) && l.EndsWith("False", StringComparison.Ordinal));
    }

    private static string[] Lines() => File.ReadAllLines(Path.Combine(Fixtures.Generated, Run));
}
