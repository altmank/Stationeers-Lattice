using System;
using System.Collections.Generic;
using Lattice.Api;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Validation;

/// <summary>
/// The game checks a completion window costs, counted on the fake, not timed: a plan's ghosts are done in batches while
/// validation still works through the game asks of runs from before, so most of the ghosts asked about leave before
/// their turn. Each check costs <see cref="CheckCost"/> on the fake clock, so the checking budget answers some a frame;
/// the worker runs every <see cref="WorkerEvery"/> frames, as a run over a large plan spans frames.
/// </summary>
public sealed class GoneGhostAskCostTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Side = 20;
    private const int DonePerFrame = 40;
    private const int WorkerEvery = 4;
    private static readonly TimeSpan CheckCost = TimeSpan.FromMilliseconds(0.05);

    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void A_completion_window_asks_the_game_only_about_ghosts_still_on_the_board()
    {
        var bench = new ValidationBench();
        var at = new Dictionary<Placement, GhostId>();
        var asked = 0;
        var gone = 0;
        bench.Placement.Rule(p =>
        {
            bench.Clock.Advance(CheckCost);
            asked++;
            if (!bench.Board.Index.Ghosts.ContainsKey(at[p]))
                gone++;
            return Option<Reason>.None;
        });
        bench.Start();
        var ghosts = new List<GhostId>(Side * Side);
        for (var i = 0; i < Side; i++)
            for (var j = 0; j < Side; j++)
            {
                var cell = Large(2 * i, 0, 2 * j);
                var ghost = bench.Ghost("StructureFrameIron", cell);
                at[((Intent.Build)bench.Board.Index.Ghosts[ghost].Ghost.Intent).Target] = ghost;
                ghosts.Add(ghost);
            }

        var frames = 0;
        for (var next = ghosts.Count; next > 0 || !bench.Settled; frames++)
        {
            for (var k = 0; k < DonePerFrame && next > 0; k++)
                Assert.True(bench.Board.Complete(ghosts[--next], new Completion(Option<StructureRef>.None, DateTimeOffset.UnixEpoch)).IsOk);
            bench.Frame(runWorker: frames % WorkerEvery == 0);
            Assert.True(frames < 1000, "the completion window did not settle");
        }

        output.WriteLine($"{ghosts.Count} ghosts done, {DonePerFrame} a frame, over {frames} frames: game asked {asked} times, {gone} about ghosts already gone");
        Assert.Equal(0, gone);
    }
}
