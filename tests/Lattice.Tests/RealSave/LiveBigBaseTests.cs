using System;
using System.IO;
using System.Linq;
using Lattice.TestKit.Fixtures;

namespace Lattice.Tests.RealSave;

/// <summary>
/// What the big-base live runs recorded on the test instance (Lattice.Game.Live <c>bigbase-build</c> and
/// <c>bigbase-load</c>; logs copied from <c>LatticeFixtures\bridge</c> by make-fixtures.ps1): a large real base (Vulcan2_4) rebuilt
/// in a vanilla world, then loaded, completed, saved and reloaded with Lattice running.
/// </summary>
public sealed class LiveBigBaseTests
{
    [SaveFact("live-bridge-bigbase-build.log")]
    public void The_base_is_rebuilt_with_a_held_back_plan_and_saved()
    {
        var log = Lines("live-bridge-bigbase-build.log");
        Assert.DoesNotContain(log, static l => l.StartsWith("LIVE FAIL", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE bigbase: plan of 5000 held-back ghosts: Applied", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE bigbase: spawned ", StringComparison.Ordinal) && l.Contains(", failed 0,"));
        Assert.Contains("LIVE bigbase: save lattice-big: success True ''", log);
    }

    [SaveFact("live-bridge-bigbase-load.log")]
    public void The_loaded_base_completes_its_plan_and_reloads_its_plans()
    {
        var log = Lines("live-bridge-bigbase-load.log");
        Assert.DoesNotContain(log, static l => l.StartsWith("LIVE FAIL", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE bigbase: load match MatchReport { Judged = ", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS plans after the autosave reload equal the plans before", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS the older save loads its plans as at the head save, the new plan hidden and not offered", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS loading ", StringComparison.Ordinal) && l.Contains("restarts Lattice"));
    }

    [SaveFact("live-bridge-bigbase-load.log")]
    public void Steady_frames_hold_the_budget_and_allocate_nothing()
    {
        var steady = Lines("live-bridge-bigbase-load.log").Single(static l => l.StartsWith("LIVE bigbase: steady:", StringComparison.Ordinal));
        var lattice = steady.Substring(steady.IndexOf("Lattice frames", StringComparison.Ordinal));
        Assert.Contains("over 6 ms 0;", lattice);
        Assert.EndsWith("one Lattice frame now allocates 0 bytes", steady);
    }

    private static string[] Lines(string file) => File.ReadAllLines(Path.Combine(Fixtures.Generated, file));
}
