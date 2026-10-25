using System;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;
using Lattice.Core.Saving;
using Lattice.Tests.Board;

namespace Lattice.Tests.Saving;

/// <summary>The inspector's read of plan files over Saving, on the Monday/Tuesday world.</summary>
public sealed class SavedPlanFilesTests : IDisposable
{
    private readonly SavedWorld _world = new();
    private readonly PlanId _hall;

    public SavedPlanFilesTests()
    {
        _world.Place(10);
        var monday = _world.Save("monday");
        _world.Place(30);
        _hall = _world.Apply(new Command.CreatePlan(new PlanDraft(
            PlanName.Create("Hall").TryGet(out var name) ? name : throw new InvalidOperationException(),
            TestBoard.TestMaker,
            new Visibility.Everyone(),
            ValueMap<AttachmentKey, Attachment>.Empty))).PlansOpened[0].Id;
        _world.Save("tuesday");
        _world.Load(Option.Some(monday));
        _world.Place(50);
    }

    private IPlanFiles Files => new SavedPlanFiles(_world.Saving);

    public void Dispose() => _world.Dispose();

    [Fact]
    public void Every_plan_with_a_file_is_listed_hidden_ones_too() =>
        Assert.Equal(new[] { GhostBoard.DefaultPlan, _hall }, Files.Plans);

    [Fact]
    public void A_plan_file_reads_every_line_of_every_branch()
    {
        Assert.True(Files.Read(GhostBoard.DefaultPlan).TryGet(out var read));
        Assert.Equal(3, read.File.Lines.Count);
        Assert.Empty(read.Problems);
    }

    [Fact]
    public void The_head_is_the_line_the_plan_is_at_and_none_for_a_hidden_plan()
    {
        Assert.Equal(Option.Some(new LineNumber(3)), Files.Head(GhostBoard.DefaultPlan));
        Assert.True(Files.Head(_hall).IsNone);
    }

    [Fact]
    public void The_saves_that_remember_a_line_of_the_plan_are_named_by_their_file()
    {
        var saves = Files.Saves(GhostBoard.DefaultPlan);

        Assert.Equal(2, saves.Count);
        Assert.Equal(("monday", 1L), (saves[0].Save.Value, saves[0].Line.Value));
        Assert.Equal(("tuesday", 2L), (saves[1].Save.Value, saves[1].Line.Value));
    }

    [Fact]
    public void A_plan_the_world_never_had_is_not_found() =>
        Assert.True(Files.Read(new PlanId(99)).IsError);
}
