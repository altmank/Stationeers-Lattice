using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Lattice.TestKit.Fixtures;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// What the looks live run recorded on the test instance (Lattice.Game.Live <c>looks</c> on a new Lunar Creative world
/// with MatterLink installed; log copied from <c>LatticeFixtures\bridge</c> by make-fixtures.ps1): every structure
/// prefab's look on a dedicated server, one modded prefab in full, and a family registered on it.
/// </summary>
public sealed class LiveLooksTests
{
    private const string Log = "live-bridge-looks.log";

    [SaveFact(Log)]
    public void Every_piece_has_a_look_and_the_two_readers_agree()
    {
        var log = Lines();

        Assert.DoesNotContain(log, static l => l.StartsWith("LIVE FAIL", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS every piece has a look: without 0", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS catalogue and mesh reader agree on every piece's source: disagree 0", StringComparison.Ordinal));
    }

    [SaveFact(Log)]
    public void The_dedicated_server_holds_meshes_for_nearly_every_piece()
    {
        var counts = Regex.Match(
            Lines().Single(static l => l.StartsWith("LIVE looks pieces ", StringComparison.Ordinal)),
            @"pieces (\d+); looks: meshes (\d+) \((\d+) meshes, (\d+) readable, (\d+) by step\), blueprint (\d+), box (\d+), cell (\d+)");
        Assert.True(counts.Success);
        var n = counts.Groups.Values.Skip(1).Select(static g => int.Parse(g.Value)).ToArray();

        Assert.Equal(n[0], n[1] + n[5] + n[6] + n[7]);
        Assert.True(n[1] >= n[0] * 0.95, $"{n[1]} of {n[0]} pieces drawn from meshes");
        Assert.True(n[4] > 0, "no per-step meshes");
    }

    [SaveFact(Log)]
    public void Every_family_found_by_type_has_pieces()
    {
        var families = Lines().Single(static l => l.StartsWith("LIVE looks families: ", StringComparison.Ordinal));

        foreach (var family in new[] { "cable", "pipe", "chute", "frame", "wall", "device", "other" })
            Assert.Matches($@"\b{family} [1-9]\d*", families);
    }

    [SaveFact(Log)]
    public void A_modded_piece_is_drawn_from_its_meshes_and_joins_a_family_until_disposed()
    {
        var log = Lines();

        Assert.Contains(log, static l => l.StartsWith("LIVE looks modded StructureMatterlink", StringComparison.Ordinal) && l.Contains("look Meshes {"));
        Assert.Contains(log, static l => l.StartsWith("LIVE looks modded part ", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS InFamily on StructureMatterlink", StringComparison.Ordinal)
                                         && l.Contains("second registration already-registered"));
    }

    [SaveFact(Log)]
    public void A_cached_look_lookup_allocates_nothing()
    {
        Assert.Contains(Lines(), static l => l.StartsWith("LIVE PASS ", StringComparison.Ordinal) && l.Contains("cached look lookups allocate 0 B"));
    }

    private static string[] Lines() => File.ReadAllLines(Path.Combine(Fixtures.Generated, Log));
}
