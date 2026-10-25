using System.Collections.Generic;
using Lattice.Api;
using static Lattice.Tests.Board.TestBoard;
using TestBoard = Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Commands;

public sealed class SubmitTests
{
    private static readonly Cell At = new(100, 100, 100);

    [Fact]
    public void A_resent_command_id_returns_the_first_outcome_and_changes_nothing()
    {
        var c = new TestCommands();
        var id = c.NextId();
        var place = new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", At)));
        var first = TestCommands.Answer(c.Service.Submit(id, place, Alice));
        c.T.Events.Flush();
        var revision = c.T.Board.Revision;
        var seen = new List<ChangeSet>();
        c.T.Events.Changed += seen.Add;

        var again = c.Service.Submit(id, place, Alice);

        Assert.Equal(new Submission.Answered(new Ticket(id), first), again);
        Assert.Equal(revision, c.T.Board.Revision);
        Assert.Single(c.T.View.At(At));
        c.T.Events.Flush();
        Assert.Empty(seen);
    }

    [Fact]
    public void A_resent_refused_command_returns_the_same_refusal_even_when_it_would_now_apply()
    {
        var c = new TestCommands();
        var id = c.NextId();
        var remove = new Command.Remove(Ids(new GhostId(1)));
        var first = TestCommands.Answer(c.Service.Submit(id, remove, Alice));
        Assert.IsType<Outcome.Refused>(first);
        Applied(c.Submit(new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", At)))));

        var again = TestCommands.Answer(c.Service.Submit(id, remove, Alice));

        Assert.Same(first, again);
        Assert.Single(c.T.View.At(At));
    }

    [Fact]
    public void A_default_command_id_is_refused()
    {
        var c = new TestCommands();

        var outcome = TestCommands.Answer(c.Service.Submit(default, new Command.Batch(ValueList<Command>.Empty), Alice));

        Assert.Equal(ReasonCodes.DefaultValue, Refused(outcome).Why.Code);
    }

    [Fact]
    public void A_command_submitted_from_the_policy_is_queued_applied_after_the_current_one_and_answered_by_event()
    {
        var c = new TestCommands();
        var answers = new List<(Ticket, Outcome)>();
        c.T.Events.Answered += (ticket, outcome) => answers.Add((ticket, outcome));
        var inner = c.NextId();
        var submittedInside = new List<Submission>();
        var revisionsSeen = new List<Revision>();
        Assert.True(c.T.Policies.Install(new AccessPolicy((_, _) => true, (_, _) => true, request =>
        {
            revisionsSeen.Add(c.T.Board.Revision);
            if (submittedInside.Count == 0)
                submittedInside.Add(c.Service.Submit(inner, new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", new Cell(200, 100, 100)))), Bob));
            return Result<Unit, Reason>.Ok(Unit.Value);
        })).IsOk);

        var outer = Applied(c.Submit(new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", At)))));

        Assert.Equal(new Submission.Sent(new Ticket(inner)), Assert.Single(submittedInside));
        Assert.Equal(new Revision(1), outer.Revision);
        Assert.Equal(new Revision(2), c.T.Board.Revision);
        Assert.Equal(new Revision(0), revisionsSeen[0]);
        Assert.Empty(answers);
        c.T.Events.Flush();
        var (ticket, outcome) = Assert.Single(answers);
        Assert.Equal(new Ticket(inner), ticket);
        Assert.Equal(new Revision(2), Applied(outcome).Revision);
        Assert.Equal(Submission(inner, outcome), c.Service.Submit(inner, new Command.Batch(ValueList<Command>.Empty), Bob));
    }

    [Fact]
    public void A_policy_that_throws_leaves_the_board_unchanged_and_the_service_usable()
    {
        var c = new TestCommands();
        var throwing = true;
        Assert.True(c.T.Policies.Install(new AccessPolicy((_, _) => true, (_, _) => true, _ =>
            throwing ? throw new System.InvalidOperationException("policy bug") : Result<Unit, Reason>.Ok(Unit.Value))).IsOk);

        Assert.Throws<System.InvalidOperationException>(() => c.Submit(new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", At)))));
        throwing = false;

        Assert.Empty(c.T.View.At(At));
        Applied(c.Submit(new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", At)))));
    }

    private static Submission Submission(CommandId id, Outcome outcome) => new Submission.Answered(new Ticket(id), outcome);

    [Fact]
    public void Commands_from_local_actors_are_not_size_checked()
    {
        var c = new TestCommands(TestCommands.Limits(maxGhosts: 1));
        var two = new Command.Place(ValueList.From([c.T.Draft("StructureCableStraight", At), c.T.Draft("StructureCableStraight", new Cell(200, 100, 100))]));

        Assert.Equal(2, Applied(c.Submit(two, TestBoard.Alice)).Added.Count);
    }
}
