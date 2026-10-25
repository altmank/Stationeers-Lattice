using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.EntryPoint;
using Lattice.Core.Styles;
using Lattice.Tests.Board;
using Lattice.Tests.EntryPoint;
using Lattice.Tests.Runs;

namespace Lattice.Tests.Styles;

/// <summary>Styles in a started world: module <c>styles</c> and <c>render</c> on, the real styles in place, fades answered by the board.</summary>
public sealed class WorldStylesTests : System.IDisposable
{
    private readonly WorldBench _bench = new();

    public void Dispose() => _bench.Dispose();

    [Fact]
    public void A_started_world_resolves_styles_and_draws()
    {
        var world = _bench.Start();

        Assert.IsType<GhostStyles>(world.Styles);
        Assert.True(world.StyleModule!.Plan.BatchCount >= 0);
        Assert.Contains(world.Modules, static m => m.Name == LatticeModules.Styles && m.State is ModuleState.On);
        Assert.Contains(world.Modules, static m => m.Name == LatticeModules.Render && m.State is ModuleState.On);
    }

    [Fact]
    public void A_ghost_placed_in_the_world_is_drawn_after_its_frame()
    {
        var world = _bench.Start();
        var placed = TestBoard.Applied(_bench.Submit(new Command.Place(ValueList.Of(_bench.Draft("ModConduitLine", RunBench.At(0, 0, 0)))))).Added[0];

        world.Frame();
        world.Frame();

        Assert.True(world.StyleModule!.Draws(placed.Id));
    }

    [Fact]
    public void Fade_ask_4_the_board_finds_remove_swap_and_finish_ghosts_on_a_structure_and_changed_reports_them()
    {
        var removed = _bench.Runs.Real("ModConduitLine", RunBench.At(10, 0, 0));
        var swapped = _bench.Runs.Real("StructureCableStraight", RunBench.At(12, 0, 0));
        var finished = _bench.Runs.Real("ModConduitLine", RunBench.At(14, 0, 0));
        var world = _bench.Start();
        var seen = new List<GhostId>();
        world.Events.Changed += changes =>
        {
            foreach (var ghost in changes.Added)
                seen.Add(ghost.Id);
        };
        var heavy = _bench.Runs.Catalogue.Named("StructureCableStraightH").Key;
        var finish = Lattice.TestKit.TestKitIds.Must(Finish.Create(Option.Some(Lattice.TestKit.TestKitIds.Must(PaintColour.Create(2))), Option<ThingName>.None, Option<Setting>.None));

        var added = TestBoard.Applied(_bench.Submit(new Command.Place(ValueList.From(new[]
        {
            TestBoard.Draft(new Intent.Remove(removed)),
            TestBoard.Draft(new Intent.Swap(swapped, new Placement(heavy, swapped.At, default), Option<Finish>.None)),
            TestBoard.Draft(new Intent.FinishOnly(finished, finish)),
        })))).Added;
        world.Frame();

        Assert.Contains(world.Board.On(removed), g => g.Id == added[0].Id);
        Assert.Contains(world.Board.On(swapped), g => g.Id == added[1].Id);
        Assert.Contains(world.Board.On(finished), g => g.Id == added[2].Id);
        Assert.Equal(new[] { added[0].Id, added[1].Id, added[2].Id }, seen);
    }
}
