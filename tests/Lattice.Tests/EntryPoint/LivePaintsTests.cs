using System;
using System.IO;
using System.Linq;
using Lattice.TestKit.Fixtures;

namespace Lattice.Tests.EntryPoint;

/// <summary>
/// What the paints live run recorded on the test instance (Lattice.Game.Live <c>paints</c> on a dedicated server; log
/// copied from <c>LatticeFixtures\bridge</c> by make-fixtures.ps1): the game's paint swatches hold real colours on a
/// server, and GamePaintColours answers each one.
/// </summary>
public sealed class LivePaintsTests
{
    private const string Log = "live-bridge-paints.log";

    [SaveFact(Log)]
    public void Every_game_paint_is_read_with_a_real_colour_on_a_server()
    {
        var log = File.ReadAllLines(Path.Combine(Fixtures.Generated, Log));

        Assert.DoesNotContain(log, static l => l.StartsWith("LIVE FAIL", StringComparison.Ordinal));
        Assert.Contains("LIVE PASS the game's paint colours are read and hold real colours", log);
        Assert.Contains(log, static l => l.StartsWith("LIVE paints 0 ColorBlue: ", StringComparison.Ordinal) && l.Contains("read Some("));
        Assert.True(log.Count(static l => l.StartsWith("LIVE paints ", StringComparison.Ordinal) && l.Contains("read Some(")) >= 12);
    }
}
