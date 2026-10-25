using System;
using System.Collections.Generic;
using Lattice.Api;
using static Lattice.Consumer.Tests.Make;

namespace Lattice.Consumer.Tests;

/// <summary>
/// The shape an add-on of rocket pieces needs, against the contract alone: a support rule, attached data on its ghosts, a
/// reaction to structures moving, and the ordinary move command.
/// </summary>
internal sealed class AddOnSketch : IDisposable
{
    private static readonly PrefabKey LaunchMount = new(5001);

    private readonly IGhostSystem _lattice;
    private readonly ModId _mod = Mod("addon");
    private readonly AttachmentKey _rocketTag;
    private readonly List<IDisposable> _registrations = new();

    public AddOnSketch(IGhostSystem lattice)
    {
        _lattice = lattice;
        _rocketTag = Ok(AttachmentKey.Create(_mod, "rocket"));

        _registrations.Add(Ok(lattice.Registries.Register(new SupportRuleDescriptor(
            Ok(RuleId.Create(_mod, "on-launch-mount")),
            piece => piece.Hash is >= 5100 and < 5200,
            OnLaunchMount))));
        _registrations.Add(Ok(lattice.Registries.Register(
            new AttachmentDescriptor(_rocketTag, Ok(DisplayName.Create("Rocket the ghost belongs to"))) { MaxLength = 32 })));
        lattice.Events.StructuresMoved += FollowRocket;
    }

    /// <summary>Tags ghosts as parts of a rocket.</summary>
    public Submission Tag(Selector ghosts, string rocket) =>
        _lattice.Commands.Submit(
            CommandId.New(),
            new Command.Change(ghosts, new GhostEdit.Attach(_rocketTag, Ok(Attachment.Create(rocket))), Option<Revision>.None),
            new Actor.Mod(_mod));

    private SupportVerdict OnLaunchMount(SupportInput input)
    {
        var below = input.World.At(input.At.At with { Y = input.At.At.Y - 20 });
        for (var i = 0; i < below.Count; i++)
            if (below[i].Ref.Piece == LaunchMount)
                return new SupportVerdict.SupportedBy(ValueList.Of(below[i].Ref));
        return new SupportVerdict.Unsupported(Reason.Of(Ok(ReasonCode.Create(_mod, "needs-launch-mount")), Ok(RuleId.Create(_mod, "on-launch-mount"))));
    }

    private void FollowRocket(ValueList<StructureMove> moves)
    {
        if (moves.Count == 0)
            return;
        var first = moves[0];
        var by = new CellOffset(first.To.X - first.From.At.X, first.To.Y - first.From.At.Y, first.To.Z - first.From.At.Z);
        _lattice.Commands.Submit(
            CommandId.New(),
            new Command.Transform(new Selector.Tagged(_rocketTag, Option<Attachment>.None), new Transformation.Move(by), new TransformMode.Move()),
            new Actor.Mod(_mod));
    }

    public void Dispose()
    {
        _lattice.Events.StructuresMoved -= FollowRocket;
        foreach (var registration in _registrations)
            registration.Dispose();
    }
}

public sealed class AddOnSketchTests
{
    [Fact]
    public void The_add_on_registers_tags_and_moves_its_ghosts_with_the_ordinary_move_command()
    {
        var lattice = new StubSystem(isHost: true);
        using var addOn = new AddOnSketch(lattice);

        addOn.Tag(new Selector.Group(Ok(GroupName.Create("Rocket 1"))), "rocket-1");
        var part = new StructureRef(new ReferenceId(9), new PrefabKey(5101), new Cell(0, 40, 0));
        lattice.Move(ValueList.Of(new StructureMove(part, new Cell(0, 2040, 0), Turn.None)));

        Assert.Single(lattice.SupportRules);
        Assert.Collection(lattice.Submitted,
            tag => Assert.IsType<GhostEdit.Attach>(Assert.IsType<Command.Change>(tag.Command).Edit),
            move => Assert.Equal(
                new Transformation.Move(new CellOffset(0, 2000, 0)),
                Assert.IsType<Command.Transform>(move.Command).How));
    }

    [Fact]
    public void Disposing_the_add_on_ends_its_registrations()
    {
        var lattice = new StubSystem(isHost: true);

        new AddOnSketch(lattice).Dispose();

        Assert.Empty(lattice.SupportRules);
    }
}
