using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Assets.Scripts;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.EntryPoint;
using Lattice.Core.Executors;
using UnityEngine;

namespace Lattice.Game.Live;

/// <summary>
/// The executor steps of the two-player run. The host registers a test executor and drives one ghost at a time
/// through claim, working, waiting and release, a lease that lapses with its executor stopped, and a claim held while
/// the host is paused; a long claim stays for the client's rejoin. At each step both seats write what they read
/// (the claim's executor, the status, the ghost's resolved look) and the status events they raised for the step's
/// ghost, and compare: the client waits until it reads what the host wrote.
/// </summary>
internal static class ExecutorSteps
{
    private static readonly List<string> Heard = new();
    private static readonly HashSet<long> Watched = new();
    private static LatticeWorld? _watching;

    /// <summary>The ghosts the steps use: the first five of the plan made before the join, the same ids on both seats.</summary>
    public static List<GhostId> Ghosts(LatticeWorld world, Func<LatticeWorld, List<GhostId>> firstOfPlan)
    {
        var ghosts = firstOfPlan(world);
        Watched.Clear();
        foreach (var ghost in ghosts)
            Watched.Add(ghost.Value);
        return ghosts;
    }

    /// <summary>Hears the world's status events for the watched ghosts; a new world (after a rejoin) is heard anew.</summary>
    public static void Watch(LatticeWorld world)
    {
        if (ReferenceEquals(_watching, world))
            return;
        _watching = world;
        world.Styles.Add(new StyleLayerDescriptor(
            StyleLayerId.Create(LatticeIdentity.Mod, "live-status").Match(static i => i, static r => throw new InvalidOperationException(r.ToString())),
            StyleBand.Theme, 0, ValueList.From(new[]
            {
                StatusEdge(StatusKind.Working, LibraryPalette.Next),
                StatusEdge(StatusKind.Waiting, LibraryPalette.Waits),
                StatusEdge(StatusKind.Paused, LibraryPalette.Select),
            })));
        world.Events.StatusChanged += ghost =>
        {
            if (Watched.Contains(ghost.Value))
                Heard.Add($"status {ghost.Value} {Kind(world.Executors.StatusOf(ghost))}");
        };
        world.Events.Blocked += (_, ghosts) =>
        {
            foreach (var blocked in ghosts)
                if (Watched.Contains(blocked.Ghost.Value))
                    Heard.Add($"blocked {blocked.Ghost.Value} {blocked.Why.Code}");
        };
    }

    public static IEnumerator Host(LatticeWorld world, List<GhostId> ghosts, string folder, Action<string, bool> check, Action<string> line)
    {
        Watch(world);
        var info = new ExecutorInfo(
            ExecutorId.Create(LatticeIdentity.Mod, "live-builder").Match(static i => i, static r => throw new InvalidOperationException(r.ToString())),
            DisplayName.Create("Live builder").Match(static n => n, static r => throw new InvalidOperationException(r.ToString())));
        if (!world.Executors.Register(info).TryGet(out var builder))
        {
            check("host: the test executor registers", false);
            yield break;
        }

        var waiting = new ExecutorStatus.Waiting(Reason.Of(ReasonCodes.NotAllowed, WorldExecutors.Rule, ValueMap.Of("needs", "steel")));
        var first = ghosts[0];
        var claim = Claim(builder, first, 600);
        yield return Step("exec-claim", "host", world, first, folder, check, line);
        builder.Report(first, new ExecutorStatus.Working(1, 3));
        yield return Step("exec-working", "host", world, first, folder, check, line);
        builder.Report(first, waiting);
        yield return Step("exec-waiting", "host", world, first, folder, check, line);
        if (claim is { } held)
            builder.Release(held);
        yield return Step("exec-release", "host", world, first, folder, check, line);

        var lapsing = ghosts[1];
        Claim(builder, lapsing, 3);
        builder.Report(lapsing, new ExecutorStatus.Working(2, 3));
        line("host: claimed a ghost for 3 s and stopped renewing it");
        yield return Until(() => world.Executors.ClaimOn(lapsing).IsNone, 60f);
        check("host: the unrenewed claim lapsed", world.Executors.ClaimOn(lapsing).IsNone);
        yield return Step("exec-lapse", "host", world, lapsing, folder, check, line);

        var paused = ghosts[2];
        var pausedClaim = Claim(builder, paused, 5);
        builder.Report(paused, new ExecutorStatus.Paused());
        var pauseThrew = "";
        try
        {
            WorldManager.SetGamePause(true);
        }
        catch (Exception e)
        {
            pauseThrew = e.GetType().Name + ": " + e.Message;
        }

        line($"host: paused the game (paused {WorldManager.IsGamePaused}{(pauseThrew.Length > 0 ? ", " + pauseThrew : "")}) with a 5 s claim; waiting 15 s real time");
        yield return new WaitForSecondsRealtime(15f);
        check($"host: the paused host holds the 5 s claim after 15 s (game time {GameManager.GameTime:F1})", world.Executors.ClaimOn(paused).IsSome);
        yield return Step("exec-paused", "host", world, paused, folder, check, line);
        try
        {
            WorldManager.SetGamePause(false);
        }
        catch (Exception e)
        {
            line($"host: unpause threw {e.GetType().Name}: {e.Message}");
        }

        if (pausedClaim is { } stillHeld)
            builder.Release(stillHeld);
        yield return Step("exec-unpaused", "host", world, paused, folder, check, line);

        var kept = ghosts[3];
        Claim(builder, kept, 3600);
        builder.Report(kept, new ExecutorStatus.Working(3, 3));
        yield return Step("exec-hold", "host", world, kept, folder, check, line);
    }

    public static IEnumerator Client(LatticeWorld world, List<GhostId> ghosts, string folder, Action<string, bool> check, Action<string> line)
    {
        Watch(world);
        foreach (var (step, ghost) in new[]
        {
            ("exec-claim", ghosts[0]), ("exec-working", ghosts[0]), ("exec-waiting", ghosts[0]), ("exec-release", ghosts[0]),
            ("exec-lapse", ghosts[1]), ("exec-paused", ghosts[2]), ("exec-unpaused", ghosts[2]), ("exec-hold", ghosts[3]),
        })
            yield return Step(step, "client", world, ghost, folder, check, line);
    }

    /// <summary>After the client's rejoin: it reads the claim held through the join, as the host wrote it.</summary>
    public static IEnumerator Rejoined(LatticeWorld world, List<GhostId> ghosts, string folder, Action<string, bool> check)
    {
        Watch(world);
        var host = Read(Path.Combine(folder, "host-exec-hold.txt")).Split('\n')[0];
        yield return Until(() => State(world, ghosts[3]) == host, 60f);
        check($"client: a client that joins while a ghost is claimed reads the claim: here '{State(world, ghosts[3])}', host '{host}'", State(world, ghosts[3]) == host);
    }

    // One step: the host has acted and writes what it reads and heard; the client waits until it reads the same, then
    // writes its own. Each compares the two.
    private static IEnumerator Step(string step, string side, LatticeWorld world, GhostId ghost, string folder, Action<string, bool> check, Action<string> line)
    {
        var mine = Path.Combine(folder, $"{side}-{step}.txt");
        var theirs = Path.Combine(folder, $"{(side == "host" ? "client" : "host")}-{step}.txt");
        if (side == "client")
        {
            yield return Until(() => File.Exists(theirs), 300f);
            var expected = Read(theirs).Split('\n')[0];
            yield return Until(() => State(world, ghost) == expected, 60f);
        }

        yield return new WaitForSecondsRealtime(1.5f);
        var heard = string.Join(" | ", Heard);
        Heard.Clear();
        File.WriteAllText(mine, State(world, ghost) + "\n" + heard + "\n" + Look(world, ghost) + "\n");
        yield return Until(() => File.Exists(theirs), 300f);
        var a = Read(mine).Split('\n');
        var b = Read(theirs).Split('\n');
        line($"{side}: {step}: reads '{a[0]}'; heard '{a[1]}'; look '{a[2]}'");
        check($"{side}: {step}: both seats read the same claim and status", a[0] == b[0]);
        check($"{side}: {step}: both seats heard the same status events", a.Length > 2 && b.Length > 2 && a[1] == b[1]);
        check($"{side}: {step}: both seats resolve the same look (here '{a[2]}', there '{(b.Length > 2 ? b[2] : "")}')", a.Length > 2 && b.Length > 2 && a[2] == b[2]);
    }

    private static Claim? Claim(IExecutorHandle builder, GhostId ghost, double seconds) =>
        builder.Claim(ghost, Lease.Create(seconds).Match(static l => l, static r => throw new InvalidOperationException(r.ToString())))
            .Match(static c => (Claim?)c, static _ => null);

    private static string State(LatticeWorld world, GhostId ghost)
    {
        var claim = world.Executors.ClaimOn(ghost).TryGet(out var c) ? "claimed by " + c.By.Value : "no claim";
        return $"{claim}; {Kind(world.Executors.StatusOf(ghost))}";
    }

    // A status outline: each executor status draws the ghost's edge in its own palette colour.
    private static StyleRule StatusEdge(StatusKind kind, PaletteKey colour) => new(
        new StyleMatch.Status(kind, Option<ExecutorId>.None),
        new Style
        {
            Edge = Option.Some<EdgeLook>(new EdgeLook.Line(
                LineStyle.Solid, Pixels.Create(3f).Match(static p => p, static r => throw new InvalidOperationException(r.ToString())), new Paint.Token(colour))),
        });

    private static string Look(LatticeWorld world, GhostId ghost) =>
        world.StyleModule is { } styles && styles.LookOf(ghost).TryGet(out var resolved)
            ? $"edge {resolved.Edge} fill {resolved.Fill} glow {resolved.Glow}"
            : "no look";

    private static string Kind(ValueList<ExecutorReport> reports)
    {
        if (reports.Count == 0)
            return "no status";
        var text = new StringBuilder();
        foreach (var report in reports)
            text.Append(report.By.Name.Value).Append(' ').Append(report.Status switch
            {
                ExecutorStatus.Working w => $"working {w.Step}/{w.Of}",
                ExecutorStatus.Paused => "paused",
                ExecutorStatus.Waiting w => "waiting " + w.Why.Code,
                _ => "unknown",
            });
        return text.ToString();
    }

    private static IEnumerator Until(Func<bool> done, float limit)
    {
        var deadline = Time.realtimeSinceStartup + limit;
        while (!done() && Time.realtimeSinceStartup < deadline)
            yield return null;
    }

    private static string Read(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }

        return "";
    }
}
