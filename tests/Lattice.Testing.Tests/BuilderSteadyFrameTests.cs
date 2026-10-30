using System;
using Lattice.Api;
using Lattice.Testing;
using Xunit.Abstractions;
using static Lattice.Testing.Tests.Hosted;

namespace Lattice.Testing.Tests;

/// <summary>
/// Builders cost nothing while nothing changes: with 64 builders registered, each with an area, a word and a supply
/// report, 20 of them assigned to plans and answered, a steady Lattice frame allocates 0 bytes, as it does with none.
/// </summary>
public sealed class BuilderSteadyFrameTests(ITestOutputHelper output)
{
    private const int Builders = 64;
    private const int Assigned = 20;
    private const int FramesPerWindow = 200;

    /// <summary>The fewest bytes a window of steady frames allocated, over three windows after warm-up.</summary>
    private static long SteadyBytes(TestWorld world)
    {
        world.Frames(FramesPerWindow);
        var least = long.MaxValue;
        for (var window = 0; window < 3 && least > 0; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            world.Frames(FramesPerWindow);
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_steady_frame_with_64_builders_and_20_assigned_allocates_nothing()
    {
        using var world = Start(TestScenario.Empty);
        var bare = SteadyBytes(world);
        var lattice = world.System;
        var handles = new IExecutorHandle[Builders];
        var item = Must(ItemQuantity.Create(new ItemKey(11), 3));
        for (var i = 0; i < Builders; i++)
        {
            var id = Must(ExecutorId.Create(Mod, "dock-" + i));
            handles[i] = Must(lattice.Executors.Register(new ExecutorInfo(id, Must(DisplayName.Create("Dock " + i))) { Owner = Option.Some(world.Viewer) }));
            handles[i].Locate(Option.Some<BuilderArea>(new BuilderArea.Sphere(new WorldPoint(i, 0, 0), Must(Metres.Create(30)), Option<Metres>.None)));
            handles[i].Say(new BuilderWord.Ready());
        }

        for (var i = 0; i < Assigned; i++)
        {
            var submitted = lattice.Commands.Submit(CommandId.New(), new Command.CreatePlan(new PlanDraft(Must(PlanName.Create("Plan " + i)), Made,
                new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty)), world.Viewer);
            var plan = Assert.IsType<Outcome.Applied>(Assert.IsType<Submission.Answered>(submitted).Outcome).Changes.PlansOpened[0].Id;
            var builder = Must(ExecutorId.Create(Mod, "dock-" + i));
            Assert.IsType<Submission.Answered>(lattice.Commands.Submit(CommandId.New(), new Command.EditPlan(plan, new PlanEdit.Assign(builder), Option<Revision>.None), world.Viewer));
            Must(handles[i].Answer(plan, new AssignmentAnswer.Accepted()));
            Must(handles[i].Offer(new SupplyReport(ValueList.Of(item), ValueMap<PlanId, ValueList<ItemQuantity>>.Of(plan, ValueList.Of(item)))));
        }

        Must(world.Settle());
        var busy = SteadyBytes(world);

        output.WriteLine($"steady frame, {FramesPerWindow} frames: {bare} bytes with no builders, {busy} bytes with {Builders} builders ({Assigned} assigned)");
        Assert.Equal(0, busy);
    }
}
