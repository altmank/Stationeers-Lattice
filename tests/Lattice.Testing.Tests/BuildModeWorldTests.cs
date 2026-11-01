using Lattice.Api;
using Lattice.Testing;
using static Lattice.Testing.Tests.Hosted;

namespace Lattice.Testing.Tests;

/// <summary>
/// A started world carries mode requests to its builders: a <see cref="Command.SetBuildMode"/> submitted through the
/// world's commands reaches the builder's handler and is answered with no board change, and its batch set follows the
/// world's board.
/// </summary>
public sealed class BuildModeWorldTests
{
    [Fact]
    public void A_world_carries_a_mode_request_to_the_builder_and_keeps_its_batch()
    {
        using var world = Start(TestScenario.Empty);
        var lattice = world.System;
        var id = Must(ExecutorId.Create(Mod, "dock"));
        var dock = Must(lattice.Executors.Register(new ExecutorInfo(id, Must(DisplayName.Create("Dock"))) { Owner = Option.Some(world.Viewer) }));
        var asked = Option<BuildMode>.None;
        Must(dock.AcceptModes(new BuildModeAccess.OwnerAndHost(), (_, mode) =>
        {
            asked = Option.Some(mode);
            return Result<Unit, Reason>.Ok(Unit.Value);
        }));
        var placed = Assert.IsType<Outcome.Applied>(Assert.IsType<Submission.Answered>(
            lattice.Commands.Submit(CommandId.New(), new Command.Place(ValueList.Of(Draft(Hand("StructureFrame"), At(0)))), world.Viewer)).Outcome).Changes;
        Must(dock.SetBatch(ValueSet.Of(placed.Added[0].Id)));
        var revision = lattice.Board.Revision;

        var outcome = Assert.IsType<Submission.Answered>(lattice.Commands.Submit(CommandId.New(), new Command.SetBuildMode(id, new BuildMode.Batch()), world.Viewer)).Outcome;
        Must(world.Settle());

        Assert.IsType<Outcome.Accepted>(outcome);
        Assert.Equal(Option.Some<BuildMode>(new BuildMode.Batch()), asked);
        Assert.Equal(revision, lattice.Board.Revision);
        Assert.Equal(1, lattice.Executors.Find(id).Reduce(() => throw new Xunit.Sdk.XunitException("no dock")).BatchLeft);
        Assert.IsType<BuildModeAccess.OwnerAndHost>(lattice.Executors.Find(id).Reduce(() => throw new Xunit.Sdk.XunitException("no dock")).ControlledBy);
    }
}
