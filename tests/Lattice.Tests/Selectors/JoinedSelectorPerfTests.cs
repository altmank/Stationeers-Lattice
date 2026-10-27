using System;
using System.Collections.Generic;
using System.Diagnostics;
using Lattice.Api;
using Lattice.TestKit;
using Xunit.Abstractions;
using static Lattice.Tests.Selectors.TestSelect;

namespace Lattice.Tests.Selectors;

/// <summary>
/// <see cref="Selector.Joined"/> over a large planned network costs time linear in the pieces it picks: a ten times
/// larger network takes well under thirty times as long.
/// </summary>
[Collection(nameof(Timing))]
public sealed class JoinedSelectorPerfTests(ITestOutputHelper output)
{
    /// <summary>
    /// A planned cable trunk up z with a junction every tenth piece and a stub off each junction's x- end, at least
    /// <paramref name="pieces"/> pieces in all.
    /// </summary>
    private static (TestSelect Select, int Count) Comb(int pieces)
    {
        var s = new TestSelect();
        s.Start();
        var drafts = new List<GhostDraft>(pieces + 11);
        for (var i = 0; drafts.Count < pieces; i++)
        {
            var at = At(0, 0, 5 * i);
            if (i % 10 == 0)
            {
                drafts.Add(s.T.Draft("StructureCableJunction", at));
                drafts.Add(s.T.Draft("StructureCableStraight", At(-5, 0, 5 * i), s.AlongX));
            }
            else
            {
                drafts.Add(s.T.Draft("StructureCableStraight", at));
            }
        }

        s.T.Placed(drafts.ToArray());
        return (s, drafts.Count);
    }

    private static TimeSpan Best(TestSelect s, Selector selector, int expected)
    {
        var best = TimeSpan.MaxValue;
        Assert.Equal(expected, Picked(s, selector));
        for (var run = 0; run < 7; run++)
        {
            Perf.Collect();
            var clock = Stopwatch.StartNew();
            Picked(s, selector);
            clock.Stop();
            if (clock.Elapsed < best)
                best = clock.Elapsed;
        }

        return best;
    }

    private static int Picked(TestSelect s, Selector selector) => TestKitIds.Must(s.Selectors.Pick(s.T.Board.Index, selector)).Count;

    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void A_ten_thousand_piece_planned_network_is_picked_in_time_linear_in_its_pieces()
    {
        var (small, smallCount) = Comb(1_000);
        var (large, largeCount) = Comb(10_000);
        var from = new Selector.Joined(At(0, 0, 5));

        var smallTime = Best(small, from, smallCount);
        var largeTime = Best(large, from, largeCount);
        var ratio = largeTime.TotalMilliseconds / smallTime.TotalMilliseconds;

        output.WriteLine($"{smallCount} pieces {smallTime.TotalMilliseconds:F3} ms; {largeCount} pieces {largeTime.TotalMilliseconds:F3} ms; ratio {ratio:F1}");
        Assert.True(ratio < 30, $"{ratio:F1} times as long for {largeCount / (double)smallCount:F1} times the pieces");
    }
}
