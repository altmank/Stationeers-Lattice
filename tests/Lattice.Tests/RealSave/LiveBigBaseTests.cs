using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Lattice.TestKit.Fixtures;

namespace Lattice.Tests.RealSave;

/// <summary>
/// What the big-base live runs recorded on the test instance (Lattice.Game.Live <c>bigbase-build</c> and
/// <c>bigbase-load</c>; the fixtures script copies the logs from the bridge folder of the fixtures): the generated big base built in a
/// new world on the VM client with 5,000 structures held back as a plan, then that world loaded, the plan completed, saved
/// and reloaded with Lattice running. Counts come from the logs.
/// </summary>
public sealed class LiveBigBaseTests
{
    [SaveFact("live-bridge-bigbase-build.log")]
    public void The_base_is_rebuilt_with_a_held_back_plan_and_saved()
    {
        var log = Lines("live-bridge-bigbase-build.log");
        Assert.DoesNotContain(log, static l => l.StartsWith("LIVE FAIL", StringComparison.Ordinal));
        var held = Number(log, @"^LIVE bigbase: plan of (\d+) held-back ghosts: Applied");
        Assert.True(held > 0);
        Assert.Contains(log, l => l.StartsWith($"LIVE bigbase: spawned ", StringComparison.Ordinal) && l.Contains(", refused 0 (prefabs the game lacks 0),"));
        Assert.Contains("LIVE bigbase: save lattice-gen-big-held: success True ''", log);
    }

    [SaveFact("live-bridge-bigbase-load.log", "live-bridge-bigbase-build.log")]
    public void The_loaded_base_completes_its_plan_and_reloads_its_plans()
    {
        var log = Lines("live-bridge-bigbase-load.log");
        Assert.DoesNotContain(log, static l => l.StartsWith("LIVE FAIL", StringComparison.Ordinal));
        Assert.DoesNotContain(log, static l => l.Contains("NOT settled", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE bigbase: load match MatchReport { Judged = ", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS plans after the autosave reload equal the plans before", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS the older save loads its plans as at the head save, the new plan hidden and not offered", StringComparison.Ordinal));
        Assert.Equal(2, log.Count(static l => l.StartsWith("LIVE PASS loading ", StringComparison.Ordinal) && l.Contains("restarts Lattice")));
    }

    [SaveFact("live-bridge-bigbase-load.log", "live-bridge-bigbase-build.log")]
    public void The_held_back_structures_spawn_inside_the_built_base_and_match_the_plan()
    {
        var load = Lines("live-bridge-bigbase-load.log");
        var held = Number(Lines("live-bridge-bigbase-build.log"), @"^LIVE bigbase: plan of (\d+) held-back ghosts: Applied");
        var spawnedLine = load.Select(static l => Regex.Match(l, @"held-back structures spawned in [\d.]+ s: (\d+) of (\d+)")).Single(static m => m.Success);
        Assert.Equal(spawnedLine.Groups[2].Value, spawnedLine.Groups[1].Value);
        Assert.InRange(int.Parse(spawnedLine.Groups[1].Value), held * 9 / 10, held * 11 / 10);
        Assert.Contains(load, static l => l.StartsWith("LIVE PASS the first held-back structure spawned at ", StringComparison.Ordinal) && l.Contains(", inside the base's footprint"));
        var judged = load.Select(static l => Regex.Match(l, @"load match MatchReport \{ Judged = (\d+)")).Where(static m => m.Success).Max(static m => int.Parse(m.Groups[1].Value));
        Assert.Equal(held, judged);
    }

    [SaveFact("live-bridge-bigbase-load.log")]
    public void Steady_frames_hold_the_budget_and_allocate_nothing()
    {
        var log = Lines("live-bridge-bigbase-load.log");
        Assert.DoesNotContain(log, static l => l.Contains("NOT settled", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE bigbase: validation of the new plan: settled after ", StringComparison.Ordinal));
        var steady = log.Single(static l => l.StartsWith("LIVE bigbase: steady:", StringComparison.Ordinal));
        var rate = Regex.Match(steady, @"cpu rate ([\d.]+) GHz");
        Assert.True(rate.Success && double.Parse(rate.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0, "the CPU time was not measured");
        var cpu = steady.Substring(steady.IndexOf("Lattice frames (cpu)", StringComparison.Ordinal));
        Assert.Contains("over 6 ms 0;", cpu);
        Assert.EndsWith("one Lattice frame now allocates 0 bytes", steady);
    }

    private static int Number(string[] log, string pattern) =>
        int.Parse(log.Select(l => Regex.Match(l, pattern)).First(static m => m.Success).Groups[1].Value);

    private static string[] Lines(string file) => File.ReadAllLines(Path.Combine(Fixtures.Generated, file));
}
