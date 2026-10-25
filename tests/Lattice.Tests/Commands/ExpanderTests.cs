using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.TestKit;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Commands;

public sealed class ExpanderTests
{
    private static readonly Cell At = new(100, 100, 100);
    private static readonly ModuleName Runs = TestKitIds.Must(ModuleName.Create("runs"));

    private static Command.PlanRun Run(Cell from, Cell to) =>
        new(new RunRequest(default, from, ValueList<Cell>.Empty, to, Option<PlanId>.None, TestMaker));

    /// <summary>A stand-in run planner: one straight at each end of the run.</summary>
    private static CommandExpander<Command.PlanRun> Planner(TestCommands c, List<ExpansionInput<Command.PlanRun>>? seen = null) => new(Runs, input =>
    {
        seen?.Add(input);
        var request = input.Command.Request;
        return Result<Command, Reason>.Ok(new Command.Batch(ValueList.From<Command>([
            new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", request.From))),
            new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", request.To))),
        ])));
    });

    [Fact]
    public void A_module_command_with_no_expander_is_refused_module_missing()
    {
        var c = new TestCommands();

        var refusal = Refused(c.Submit(Run(At, new Cell(200, 100, 100))));

        Assert.Equal(ReasonCodes.ModuleMissing, refusal.Why.Code);
        Assert.True(refusal.Why.Facts.TryGetValue("command", out var command) && command == "PlanRun");
    }

    [Fact]
    public void A_registered_expander_turns_the_command_into_primitive_changes_applied_as_one_change_set()
    {
        var c = new TestCommands();
        var seen = new List<ExpansionInput<Command.PlanRun>>();
        Assert.True(c.Expanders.Register(Planner(c, seen)).IsOk);
        var id = c.NextId();

        var changes = Applied(TestCommands.Answer(c.Service.Submit(id, Run(At, new Cell(200, 100, 100)), Alice)));

        Assert.Equal(2, changes.Added.Count);
        Assert.Equal(id, changes.Command);
        var input = Assert.Single(seen);
        Assert.Equal(id, input.Id);
        Assert.Equal(Alice, input.Actor);
        Assert.Same(c.T.Board.ViewFor(Alice), input.Board);
    }

    [Fact]
    public void A_module_command_inside_a_batch_is_expanded_too()
    {
        var c = new TestCommands();
        Assert.True(c.Expanders.Register(Planner(c)).IsOk);

        var changes = Applied(c.Submit(new Command.Batch(ValueList.From<Command>([
            Run(At, new Cell(200, 100, 100)),
            new Command.Place(ValueList.Of(c.T.Draft("StructureCableCorner", new Cell(300, 100, 100)))),
        ]))));

        Assert.Equal(3, changes.Added.Count);
    }

    [Fact]
    public void An_expanders_refusal_refuses_the_command_with_its_reason()
    {
        var c = new TestCommands();
        var blocked = TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "no-route"));
        Assert.True(c.Expanders.Register(new CommandExpander<Command.PlanRun>(Runs, _ => Result<Command, Reason>.Error(Reason.Of(blocked, RuleId.Contract)))).IsOk);

        Assert.Equal(blocked, Refused(c.Submit(Run(At, At))).Why.Code);
    }

    [Fact]
    public void A_second_expander_for_a_kind_is_refused_and_disposing_frees_the_kind()
    {
        var c = new TestCommands();
        var first = c.Expanders.Register(Planner(c));
        Assert.True(first.TryGet(out var registration));

        Assert.Equal(ReasonCodes.AlreadyRegistered, c.Expanders.Register(Planner(c)).ErrorOf().Code);
        registration.Dispose();

        Assert.Equal(ReasonCodes.ModuleMissing, Refused(c.Submit(Run(At, At))).Why.Code);
        Assert.True(c.Expanders.Register(Planner(c)).IsOk);
    }

    [Fact]
    public void Disposing_an_old_registration_leaves_a_newer_one_in_place()
    {
        var c = new TestCommands();
        Assert.True(c.Expanders.Register(Planner(c)).TryGet(out var old));
        old.Dispose();
        Assert.True(c.Expanders.Register(Planner(c)).IsOk);

        old.Dispose();

        Assert.IsType<Outcome.Applied>(c.Submit(Run(At, new Cell(200, 100, 100))));
    }

    [Fact]
    public void Primitive_kinds_take_no_expander()
    {
        var c = new TestCommands();

        var refused = c.Expanders.Register(new CommandExpander<Command.Place>(Runs, input => Result<Command, Reason>.Ok(input.Command)));

        Assert.Equal(ReasonCodes.InvalidValue, refused.ErrorOf().Code);
    }

    [Fact]
    public void An_expansion_that_never_reaches_primitive_commands_is_refused()
    {
        var c = new TestCommands();
        Assert.True(c.Expanders.Register(new CommandExpander<Command.PlanRun>(Runs, input => Result<Command, Reason>.Ok(input.Command))).IsOk);

        var refusal = Refused(c.Submit(Run(At, At)));

        Assert.Equal(ReasonCodes.TooLarge, refusal.Why.Code);
        Assert.True(refusal.Why.Facts.ContainsKey("expansion-depth"));
    }

    [Fact]
    public void An_expander_that_throws_leaves_the_board_unchanged()
    {
        var c = new TestCommands();
        Assert.True(c.Expanders.Register(new CommandExpander<Command.PlanRun>(Runs, _ => throw new InvalidOperationException("planner bug"))).IsOk);
        var revision = c.T.Board.Revision;

        Assert.Throws<InvalidOperationException>(() => c.Submit(Run(At, At)));

        Assert.Equal(revision, c.T.Board.Revision);
        Assert.IsType<Outcome.Applied>(c.Submit(new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", At)))));
    }
}
