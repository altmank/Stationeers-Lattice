using System;
using System.IO;
using System.Linq;
using Lattice.TestKit.Fixtures;

namespace Lattice.Tests.World;

/// <summary>
/// What the live runs of the world bridge in the game recorded (Lattice.Game.Live on the test instance; logs copied by
/// make-fixtures.ps1): the port suites passed in a new world and after its save loaded, the removal, broken and move
/// signals, and the game's answers at known accepted and refused spots, a modded piece included.
/// </summary>
public sealed class LiveRunTests
{
    private const string NewRun = "live-bridge-new.log";
    private const string LoadRun = "live-bridge-load.log";

    [SaveFact(NewRun)]
    public void The_new_world_run_finished_with_no_failure() => Finished(NewRun);

    [SaveFact(LoadRun)]
    public void The_loaded_world_run_finished_with_no_failure() => Finished(LoadRun);

    [SaveFact(NewRun)]
    public void Removal_broken_move_and_setting_signals_passed_in_the_game()
    {
        var log = Lines(NewRun);
        Assert.Contains(log, l => l.StartsWith("LIVE PASS hand removal gives only removed", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("LIVE PASS broken by damage gives state broken", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("LIVE PASS detach and attach give one move", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("LIVE PASS a logic setting gives setting changed", StringComparison.Ordinal));
    }

    [SaveFact(NewRun)]
    public void Known_spots_give_the_games_answers()
    {
        var log = Lines(NewRun);
        Assert.Contains("LIVE placement frame in free air: accepted", log);
        Assert.Contains("LIVE placement frame in a frame's cell: refused game-refused 'Placement is blocked by Steel Frame'", log);
        Assert.Contains("LIVE placement area power control with no wall: refused game-refused 'Placement requires support'", log);
        Assert.Contains("LIVE placement pipe on the cable's axis, same cell: refused game-refused 'Placement is blocked by Cable (Straight)'", log);
        Assert.Contains("LIVE placement pipe turned a quarter, same cell: accepted", log);
        Assert.Contains("LIVE placement matterlink capacitor in free air: accepted", log);
        Assert.Contains("LIVE placement a piece with no cursor: refused unknown-piece ''", log);
    }

    [SaveFact(NewRun)]
    public void Ground_pieces_are_answered_without_a_player_nearby()
    {
        var log = Lines(NewRun);
        Assert.Contains(log, l => l.StartsWith("LIVE terrain: surface voxel", StringComparison.Ordinal) && l.EndsWith("a collider there: False", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("LIVE terrain placement small flag on the terrain", StringComparison.Ordinal) && l.EndsWith(": accepted", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("LIVE terrain placement small flag in free air", StringComparison.Ordinal) && l.Contains("refused game-refused"));
    }

    private static void Finished(string file)
    {
        var log = Lines(file);
        Assert.DoesNotContain(log, l => l.Contains(" FAIL "));
        Assert.Contains("LIVE done", log);
        var suites = log.Single(l => l.StartsWith("LIVE SUITES ", StringComparison.Ordinal)).Split(' ');
        Assert.True(int.Parse(suites[3]) >= 42, string.Join(" ", suites));
        Assert.Equal("0", suites[5]);
    }

    private static string[] Lines(string file) => File.ReadAllLines(Path.Combine(Fixtures.Generated, file));
}
